using GroceryScout.Core;
using GroceryScout.Providers;

// Usage: SpikeRunner dump <url> [--wait=seconds] [--selector=css]   (saves screenshot/HTML to the debug folder)
if (args.Length >= 2 && args[0] == "dump")
{
    var waitSeconds = int.Parse(args.FirstOrDefault(a => a.StartsWith("--wait="))?.Substring(7) ?? "6");
    var cssSelector = args.FirstOrDefault(a => a.StartsWith("--selector="))?.Substring(11);
    await using var dumpBrowser = await BrowserSession.ConnectAsync("http://localhost:9222");
    var dumpPage = await dumpBrowser.NewPageAsync();
    await dumpPage.BringToFrontAsync();
    await dumpPage.SetViewportSizeAsync(1366, 850);
    await dumpPage.GotoAsync(args[1], new() { WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded });
    await dumpPage.WaitForTimeoutAsync(waitSeconds * 1000);
    var clickSelector = args.FirstOrDefault(a => a.StartsWith("--click="))?.Substring(8);
    if (clickSelector is not null)
    {
        await dumpPage.Locator(clickSelector).First.ClickAsync(new() { Timeout = 10_000 });
        Console.WriteLine($"Clicked {clickSelector}");
        await dumpPage.WaitForTimeoutAsync(2500);
    }
    var dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GroceryScout", "debug");
    Directory.CreateDirectory(dumpDir);
    await dumpPage.ScreenshotAsync(new() { Path = Path.Combine(dumpDir, "dump.png") });
    await File.WriteAllTextAsync(Path.Combine(dumpDir, "dump.html"), await dumpPage.ContentAsync());
    Console.WriteLine($"Title: {await dumpPage.TitleAsync()}\nUrl: {dumpPage.Url}");
    if (cssSelector is not null)
    {
        var matches = await dumpPage.Locator(cssSelector).AllAsync();
        Console.WriteLine($"{matches.Count} matches for {cssSelector}");
        for (var i = 0; i < Math.Min(3, matches.Count); i++)
        {
            var html = await matches[i].EvaluateAsync<string>("e => e.outerHTML");
            // Hidden form fields and CSRF meta tags on some pages carry per-session tokens; blank them so a dump is safe to keep or share.
            if (args.Contains("--sanitize"))
            {
                html = System.Text.RegularExpressions.Regex.Replace(html, "(<input[^>]*type=\"hidden\"[^>]*value=\")[^\"]*(\")", "$1x$2");
                html = System.Text.RegularExpressions.Regex.Replace(html, "(<meta[^>]*name=\"[^\"]*csrf[^\"]*\"[^>]*content=\")[^\"]*(\")", "$1x$2");
            }
            await File.WriteAllTextAsync(Path.Combine(dumpDir, $"dump-match{i}.html"), html);
        }
    }
    return 0;
}

// Usage: SpikeRunner <pincode> "<item with size>" [--add]
//   e.g. SpikeRunner 560001 "turmeric powder 200g" --add
if (args.Length < 2)
{
    Console.WriteLine("Usage: SpikeRunner <pincode> \"<item with size>\" [--add]");
    return 1;
}

var pincode = args[0];
var itemText = args[1];
var addToCart = args.Contains("--add");

var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "GroceryScout", "profiles", "bigbasket");
// Optional: --provider=bigbasket|amazon (default bigbasket)
var providerArg = args.FirstOrDefault(a => a.StartsWith("--provider="))?.Substring("--provider=".Length) ?? "bigbasket";
var selectorFile = Path.Combine(AppContext.BaseDirectory, $"{providerArg}.selectors.json");

// Optional: --cdp=http://localhost:9222 attaches to your own logged-in Chrome (see README)
// Optional: --channel=msedge|chrome|none (default chrome) when launching a browser ourselves
var cdpArg = args.FirstOrDefault(a => a.StartsWith("--cdp="))?.Substring("--cdp=".Length);
var channelArg = args.FirstOrDefault(a => a.StartsWith("--channel="))?.Split('=')[1] ?? "chrome";
await using var browser = cdpArg is not null
    ? await BrowserSession.ConnectAsync(cdpArg)
    : await BrowserSession.StartAsync(profile, channel: channelArg == "none" ? null : channelArg);
IProviderAdapter adapter = providerArg.ToLowerInvariant() switch
{
    "bigbasket" => new BigBasketAdapter(browser, SelectorConfig.LoadOrDefault(selectorFile, BigBasketAdapter.Defaults)),
    "amazon" => new AmazonAdapter(browser, SelectorConfig.LoadOrDefault(selectorFile, AmazonAdapter.Defaults)),
    "blinkit" => new BlinkitAdapter(browser, SelectorConfig.LoadOrDefault(selectorFile, BlinkitAdapter.Defaults)),
    _ => throw new ArgumentException($"Unknown provider '{providerArg}'"),
};

Console.WriteLine($"Provider: {adapter.Name}. Make sure you are logged in to it in the browser, then press Enter...");
Console.ReadLine();

if (args.Contains("--skip-location"))
    Console.WriteLine("Skipping location (using the location already set in the browser).");
else
{
    Console.WriteLine($"Setting location {pincode}...");
    await adapter.SetLocationAsync(pincode);
}

var hasSize = SizeParser.TryParse(itemText, out var size, out var unit);
var request = new ItemRequest(itemText, hasSize ? size : null, hasSize ? unit : null);

Console.WriteLine($"Searching '{itemText}'...");
var allListings = await adapter.SearchAsync(itemText);
var listings = QueryMatcher.FilterRelevant(itemText, allListings);
Console.WriteLine($"Parsed {allListings.Count} listings, {listings.Count} relevant to '{itemText}':");
foreach (var l in listings)
    Console.WriteLine($"  {l.Title} | {l.SizeValue}{l.Unit} | Rs {l.Price} | stock={l.InStock}{(l.Purchased ? $" | PURCHASED {l.PurchasedNote}" : "")} | {l.Url}");

var resolution = SizeResolver.Resolve(request, listings);
Console.WriteLine($"Resolution: {resolution.Kind}");
if (resolution.Chosen is { } chosen)
{
    Console.WriteLine($"Chosen: {chosen.Title} ({chosen.SizeValue}{chosen.Unit}) Rs {chosen.Price}");
    if (addToCart)
    {
        var result = await adapter.AddToCartAsync(chosen, request.Count);
        Console.WriteLine(result.Success ? "Added to cart." : $"Add to cart failed: {result.Error}");
    }
}

Console.WriteLine("Press Enter to close the browser...");
Console.ReadLine();
return 0;
