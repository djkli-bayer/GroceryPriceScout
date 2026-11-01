using System.Globalization;
using System.Text.RegularExpressions;
using GroceryScout.Core;
using Microsoft.Playwright;

namespace GroceryScout.Providers;

/// <summary>
/// Generic selector-driven adapter. Provider-specific subclasses supply the name and default selectors.
/// </summary>
public abstract partial class SelectorDrivenAdapter : IProviderAdapter, ISearchLimit
{
    private readonly BrowserSession _browser;
    protected readonly SelectorConfig Config;
    private IPage? _page;

    public abstract string Name { get; }

    /// <summary>How many searches may run in parallel tabs for this provider (see <see cref="ISearchLimit"/>).</summary>
    public virtual int MaxConcurrentSearches => 3;

    /// <summary>Pause after each scroll step so lazily loaded cards can render.</summary>
    protected virtual int ScrollWaitMs => 800;

    /// <summary>How far each scroll step moves, as a fraction of the viewport height (below 1 so no row is skipped).</summary>
    protected virtual double ScrollStep => 0.9;

    protected SelectorDrivenAdapter(BrowserSession browser, SelectorConfig config)
    {
        _browser = browser;
        Config = config;
    }

    protected async Task<IPage> PageAsync()
    {
        if (_page is { IsClosed: false }) return _page;
        _page = await _browser.NewPageAsync();
        await _page.SetViewportSizeAsync(1366, 850);
        await _page.GotoAsync(Config.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        return _page;
    }

    public virtual async Task SetLocationAsync(string pincode, CancellationToken ct = default)
    {
        var page = await PageAsync();
        await VisibleFirst(page, Config.LocationButton).ClickAsync();
        var input = VisibleFirst(page, Config.LocationInput);
        await input.FillAsync(pincode);
        var suggestion = VisibleFirst(page, Config.LocationSuggestion);
        await suggestion.WaitForAsync(new() { Timeout = 10_000 });
        await suggestion.ClickAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    private static ILocator VisibleFirst(IPage page, string selector) =>
        page.Locator(selector).Filter(new() { Visible = true }).First;

    public virtual async Task<IReadOnlyList<ProductListing>> SearchAsync(string query, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var page = await PageAsync();
        Trace($"page ready {sw.ElapsedMilliseconds} ms");
        var url = string.Format(Config.SearchUrlTemplate, Uri.EscapeDataString(query));
        var results = new List<ProductListing>();

        // Single-page sites sometimes never render while their tab is in the background. If no product card shows up,
        // bring the tab forward and load the page once more before giving up. (Not done up front: the stores' tabs
        // would then steal focus from each other mid-scroll, which stalls lazy loading in the tab that lost it.)
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 1) await page.BringToFrontAsync();
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            Trace($"navigated (attempt {attempt}) {sw.ElapsedMilliseconds} ms");
            try
            {
                await page.Locator(Config.ProductCard).First.WaitForAsync(new() { Timeout = 15_000 });
                Trace($"first card {sw.ElapsedMilliseconds} ms");
                await CollectWhileScrollingAsync(page, query, results, ct);
                Trace($"scrolled/collected {sw.ElapsedMilliseconds} ms, {results.Count} listings");
                break;
            }
            catch (TimeoutException) when (attempt < MaxSearchAttempts) { }
            catch (TimeoutException)
            {
                await DumpDebugAsync(page, query);
                throw new InvalidOperationException(
                    "No products were shown (the page did not load in time, or nothing matched this search).");
            }
        }

        if (results.Count == 0) await DumpDebugAsync(page, query);
        return results;
    }

    protected virtual int MaxSearchAttempts => 2;

    private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("GROCERYSCOUT_TRACE") == "1";

    private void Trace(string message)
    {
        if (TraceEnabled) Console.WriteLine($"[trace {Name}] {message}");
    }

    // Scrolls and reports whether the last result card was already fully in view BEFORE the move, i.e. whether
    // everything currently loaded has been seen. (The end of the whole page is far below the results: footers, carousels.)
    // "jump" positions the end of the last card mid-screen (revealing any load-more trigger below it) instead of
    // stepping through content that needs no rendering.
    private const string ScrollScript = """
        ([sel, step, jump]) => {
          const cards = document.querySelectorAll(sel);
          const last = cards.length ? cards[cards.length - 1] : null;
          const lastCardSeen = !last || last.getBoundingClientRect().bottom <= window.innerHeight + 5;
          const atPageBottom = window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 5;
          if (jump && last) window.scrollTo(0, window.scrollY + last.getBoundingClientRect().bottom - window.innerHeight * 0.5);
          else window.scrollBy(0, window.innerHeight * step);
          return lastCardSeen || atPageBottom;
        }
        """;

    /// <summary>
    /// Some result pages keep loading unrelated recommendations after the relevant products. When this many scroll
    /// rounds pass without a new listing (and we already have some), stop. Off by default: stopping early is only safe
    /// for providers whose results are ordered by relevance, because a lazy-loading stall looks the same as "no more".
    /// Weak matches far down the page can be missed.
    /// </summary>
    protected virtual int StaleRoundsLimit => int.MaxValue;

    /// <summary>How many times a card may be read while on screen without becoming parsable before it is given up on.</summary>
    protected virtual int MaxOnScreenAttempts => 2;

    /// <summary>
    /// Consecutive scroll rounds with nothing new, at the end of the loaded cards, before the scan stops. Lazy loading can
    /// be slow (especially in a background tab), so this is a grace period rather than a guarantee.
    /// </summary>
    protected virtual int IdleRoundsLimit => 4;

    // The grid lazy-loads and virtualizes (off-screen cards are emptied), so read the cards rendered after every
    // scroll step (one browser call for all of them) and merge by product id.
    private async Task CollectWhileScrollingAsync(IPage page, string query, List<ProductListing> results, CancellationToken ct, int maxRounds = 40)
    {
        var seen = new HashSet<string>();
        var attempts = new Dictionary<string, int>(); // on-screen read attempts per not-yet-parsable card
        var dumped = false;
        var idleRounds = 0;
        var lastCardCount = -1;
        var lastGrowthRound = 0;
        for (var round = 0; round < maxRounds && idleRounds < IdleRoundsLimit; round++)
        {
            ct.ThrowIfCancellationRequested();
            var roundTimer = System.Diagnostics.Stopwatch.StartNew();
            var before = results.Count;

            IReadOnlyList<CardData> cards;
            var extractTimer = System.Diagnostics.Stopwatch.StartNew();
            try { cards = await CardExtractor.ExtractAsync(page, Config); }
            catch (PlaywrightException) { cards = []; } // the page navigated or re-rendered mid-read; try again next round
            var extractMs = extractTimer.ElapsedMilliseconds;

            if (TraceEnabled && !dumped && cards.Count > 0) { await DumpFirstCardsAsync(page); dumped = true; }

            // Cards that still need to be scrolled to / waited on: empty ones, or ones that could not be parsed yet and
            // have not had a fair chance on screen. (A card that stays unparsable after being seen is given up on.)
            var buildTimer = System.Diagnostics.Stopwatch.StartNew();
            var unhandled = 0;
            foreach (var card in cards)
            {
                var key = ListingBuilder.KeyOf(card, Config);
                if (key is not null && seen.Contains(key)) continue;

                // A card that rendered but is irrelevant is "handled"; one that has not rendered yet is retried next round.
                var parsed = ListingBuilder.Build(card, Config, Name, query);
                if (TraceEnabled && parsed.Listing is null && round == 0)
                    Trace($"  skipped: rendered={parsed.Rendered} title='{card.Title}' price='{card.Price}' size='{card.Size}' key={key}");
                if (parsed.Listing is not null) results.Add(parsed.Listing);
                if (parsed.Rendered && key is not null) { seen.Add(key); continue; }

                if (key is null) { unhandled++; continue; } // empty (virtualized) card
                var tries = attempts.GetValueOrDefault(key) + (card.InView ? 1 : 0);
                attempts[key] = tries;
                if (tries < MaxOnScreenAttempts) unhandled++;
            }
            if (results.Count > before) lastGrowthRound = round;
            var buildMs = buildTimer.ElapsedMilliseconds;
            Trace($"round {round}: {cards.Count} cards ({unhandled} unhandled), {results.Count} listings, extract {extractMs} ms");

            // Enough: we have results and the page has only shown unrelated products for a while.
            if (results.Count > 0 && unhandled == 0 && round - lastGrowthRound >= StaleRoundsLimit) break;

            // Done when, having seen the end of the loaded cards, nothing new (listings or cards) appears for a few
            // rounds in a row; lazy loading can lag behind a fast scan.
            var jump = unhandled == 0 && cards.Count > 0;
            var scrollTimer = System.Diagnostics.Stopwatch.StartNew();
            var reachedEnd = await page.EvaluateAsync<bool>(ScrollScript, new object[] { Config.ProductCard, ScrollStep, jump });
            var scrollMs = scrollTimer.ElapsedMilliseconds;
            Trace($"  round {round} timings: extract {extractMs} ms, build {buildMs} ms, scroll {scrollMs} ms, round so far {roundTimer.ElapsedMilliseconds} ms (jump={jump}, reachedEnd={reachedEnd})");
            var idle = results.Count == before && cards.Count == lastCardCount && reachedEnd;
            idleRounds = idle ? idleRounds + 1 : 0;
            lastCardCount = cards.Count;
            await page.WaitForTimeoutAsync(ScrollWaitMs);
        }
    }

    private static string DebugDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GroceryScout", "debug");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task DumpFirstCardsAsync(IPage page)
    {
        var html = await page.Locator(Config.ProductCard).EvaluateAllAsync<string[]>("els => els.slice(0, 3).map(e => e.outerHTML)");
        for (var i = 0; i < html.Length; i++)
            await File.WriteAllTextAsync(Path.Combine(DebugDir(), $"{Name}-card{i}.html"), html[i]);
    }

    private async Task DumpDebugAsync(IPage page, string query)
    {
        var stem = Path.Combine(DebugDir(), $"{Name}-{DateTime.Now:HHmmss}");
        await page.ScreenshotAsync(new() { Path = stem + ".png" });
        await File.WriteAllTextAsync(stem + ".html", await page.ContentAsync());
        Console.WriteLine($"No listings parsed for '{query}'. Debug files: {stem}.png / .html");
    }

    /// <summary>"Purchased Jan 2025" -> "Jan 2025"; "Purchased 1 time" -> "1 time".</summary>
    public static string? PurchasedNote(string? badgeText)
    {
        if (string.IsNullOrWhiteSpace(badgeText)) return null;
        var note = badgeText.Trim();
        if (note.StartsWith("Purchased", StringComparison.OrdinalIgnoreCase)) note = note["Purchased".Length..].Trim();
        return note.Length == 0 ? null : note;
    }

    public virtual async Task<CartResult> AddToCartAsync(ProductListing listing, int quantity, CancellationToken ct = default)
    {
        if (Config.ProductPageAddButton.Length == 0)
            return new CartResult(false, $"No product-page add button is configured for {Name}.");

        try
        {
            var page = await PageAsync();
            await page.GotoAsync(listing.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            if (Config.ProductPageQuantitySelect.Length > 0 && quantity > 1)
                await page.Locator(Config.ProductPageQuantitySelect).First.SelectOptionAsync(quantity.ToString(), new() { Timeout = 10_000 });
            await page.Locator(Config.ProductPageAddButton).First.ClickAsync(new() { Timeout = 10_000 });
            if (Config.ProductPageQuantitySelect.Length == 0)
                for (var i = 1; i < quantity; i++)
                    await page.Locator(Config.QuantityIncrement).First.ClickAsync(new() { Timeout = 10_000 });
            await page.WaitForTimeoutAsync(1500);
            await page.ScreenshotAsync(new() { Path = Path.Combine(DebugDir(), $"{Name}-after-add.png") });
            return new CartResult(true);
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            return new CartResult(false, ex.Message);
        }
    }

    public static decimal? ParsePrice(string? text)
    {
        if (text is null) return null;
        var m = PriceRegex().Match(text.Replace(",", ""));
        return m.Success ? decimal.Parse(m.Value, CultureInfo.InvariantCulture) : null;
    }

    public static string ProductIdFromUrl(string url)
    {
        var m = ProductIdRegex().Match(url);
        return m.Success ? m.Groups[1].Value : url;
    }

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex PriceRegex();

    [GeneratedRegex(@"/pd/(\d+)")]
    private static partial Regex ProductIdRegex();
}
