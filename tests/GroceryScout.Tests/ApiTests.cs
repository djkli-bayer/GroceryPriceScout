using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GroceryScout.Providers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GroceryScout.Tests;

/// <summary>End-to-end API tests against an in-memory server with a temp database and an unreachable browser.</summary>
public sealed class ApiTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"groceryscout-api-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiTests() : this(apiKey: "") { }

    private ApiTests(string apiKey)
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("GroceryScout:DatabasePath", _db);
            b.UseSetting("GroceryScout:BrowserCdpUrl", "http://127.0.0.1:1"); // never touch a real browser
            b.UseSetting("GroceryScout:UseLlm", "false");
            b.UseSetting("GroceryScout:ApiKey", apiKey);
        });
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
            File.Delete(f);
    }

    private static async Task<JsonNode> Json(HttpResponseMessage r) =>
        JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task Health_ReportsBrowserDownAndNoLocation()
    {
        var r = await _client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.False((bool)j["browser"]!);
        Assert.False((bool)j["llmEnabled"]!);
        Assert.False((bool)j["hasDefaultLocation"]!);
    }

    [Fact]
    public async Task ParseList_ReturnsStructuredItems()
    {
        var r = await _client.PostAsJsonAsync("/api/lists/parse", new { text = "turmeric powder 200g x2\nmilk 1l" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var items = (await Json(r))["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal("turmeric powder", (string)items[0]!["item"]!);
        Assert.Equal(200m, (decimal)items[0]!["sizeValue"]!);
        Assert.Equal("Gram", (string)items[0]!["unit"]!);
        Assert.Equal(2, (int)items[0]!["count"]!);
        Assert.Equal("Millilitre", (string)items[1]!["unit"]!);
    }

    [Fact]
    public async Task ParseList_RejectsEmptyText()
    {
        var r = await _client.PostAsJsonAsync("/api/lists/parse", new { text = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Locations_CrudAndValidation()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/locations", new { label = "Home", pincode = "12ab" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/locations", new { label = " ", pincode = "560100" })).StatusCode);

        var created = await _client.PostAsJsonAsync("/api/locations", new { label = "Home", pincode = "560100" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (long)(await Json(created))["id"]!;

        var list = (await Json(await _client.GetAsync("/api/locations"))).AsArray();
        Assert.Equal("560100", (string)list.Single()!["pincode"]!);
        Assert.True((bool)list.Single()!["isDefault"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/locations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/locations/{id}")).StatusCode);
    }

    [Fact]
    public async Task Providers_ListToggleAndSelectorOverrideValidation()
    {
        var list = (await Json(await _client.GetAsync("/api/providers"))).AsArray();
        Assert.Equal(["Amazon", "BigBasket", "Blinkit"], list.Select(p => (string)p!["name"]!));

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PutAsJsonAsync("/api/providers/amazon/enabled", new { enabled = false })).StatusCode);
        var after = (await Json(await _client.GetAsync("/api/providers"))).AsArray();
        Assert.False((bool)after.Single(p => (string)p!["name"]! == "Amazon")!["enabled"]!);

        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PutAsJsonAsync("/api/providers/nope/enabled", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PutAsJsonAsync("/api/providers/blinkit/selectors", new { selectorConfigJson = "{ not json" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PutAsJsonAsync("/api/providers/blinkit/selectors", new { selectorConfigJson = """{"cardTitle":"h1"}""" })).StatusCode);
    }

    [Fact]
    public async Task CreateRun_ValidatesInput()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/runs", new { })).StatusCode);
        // no pincode and no default location
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/runs", new { text = "rice 1kg" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/runs", new { text = "rice 1kg", pincode = "abc" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/runs", new { items = new[] { new { item = "rice", count = 0 } }, pincode = "560100" })).StatusCode);
    }

    [Fact]
    public async Task CreateRun_RefusesLargeCountsReadFromFreeText_WhenAddingToCart()
    {
        await _client.PostAsJsonAsync("/api/locations", new { label = "Home", pincode = "560100" });

        var refused = await _client.PostAsJsonAsync("/api/runs", new { text = "bananas x12", addToCart = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("explicitly", (string)(await Json(refused))["error"]!);

        // A dry run, or explicitly reviewed items, is fine.
        Assert.Equal(HttpStatusCode.Accepted,
            (await _client.PostAsJsonAsync("/api/runs", new { text = "bananas x12", addToCart = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await _client.PostAsJsonAsync("/api/runs", new { items = new[] { new { item = "bananas", count = 12 } }, addToCart = true })).StatusCode);
    }

    private static GroceryScout.Core.ProductListing Listing(string provider, string id, decimal size, decimal price) =>
        new(provider, $"Turmeric {id}", null, size, GroceryScout.Core.SizeUnit.Gram, price, null, true, $"https://x/{id}", id);

    private async Task<long> SeedFinishedRunAsync(GroceryScout.Core.MatchKind match, GroceryScout.Core.CartStatus cart, int count = 2)
    {
        var store = _factory.Services.GetRequiredService<GroceryScout.Data.Store>();
        var item = new GroceryScout.Core.ItemRequest("turmeric powder", 200, GroceryScout.Core.SizeUnit.Gram, count);
        var runId = await store.CreateRunAsync("turmeric powder 200g", "560100", false, [item, item]);
        var small = Listing("Blinkit", "small", 100, 40);
        var other = Listing("BigBasket", "other", 100, 45);
        await store.SaveOutcomeAsync(runId, new GroceryScout.Core.ItemOutcome(0, item,
            new GroceryScout.Core.Resolution(match, small, [small, other]), cart, null, []));
        await store.SetRunStatusAsync(runId, GroceryScout.Data.RunStatus.Completed);
        return runId; // item 1 is left Pending on purpose
    }

    private async Task<JsonNode> WaitForCartAsync(long runId, int index, params string[] untilAnyOf)
    {
        JsonNode? item = null;
        for (var i = 0; i < 100; i++)
        {
            var run = await Json(await _client.GetAsync($"/api/runs/{runId}"));
            item = run["items"]![index]!;
            if (untilAnyOf.Contains((string?)item["outcome"]?["cart"])) break;
            await Task.Delay(100);
        }
        return item!;
    }

    [Fact]
    public async Task AddToCart_ApprovedSmallerPack_IsQueuedAndReportsFailureWhenBrowserIsDown()
    {
        var runId = await SeedFinishedRunAsync(GroceryScout.Core.MatchKind.SmallerSize, GroceryScout.Core.CartStatus.SkippedNeedsApproval);

        var resp = await _client.PostAsync($"/api/runs/{runId}/items/0/add-to-cart", null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var item = await WaitForCartAsync(runId, 0, "Failed", "Added");
        Assert.Equal("Failed", (string)item["outcome"]!["cart"]!);
        Assert.Contains("start-chrome", (string)item["outcome"]!["cartError"]!);
        Assert.Equal("small", (string)item["outcome"]!["chosen"]!["productId"]!);
    }

    [Fact]
    public async Task AddToCart_CanPickAnotherOption_WhichBecomesTheChosenOne()
    {
        var runId = await SeedFinishedRunAsync(GroceryScout.Core.MatchKind.Exact, GroceryScout.Core.CartStatus.NotAttempted);

        var resp = await _client.PostAsJsonAsync($"/api/runs/{runId}/items/0/add-to-cart", new { provider = "BigBasket", productId = "other" });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var item = await WaitForCartAsync(runId, 0, "Failed", "Added");
        Assert.Equal("other", (string)item["outcome"]!["chosen"]!["productId"]!);
    }

    [Fact]
    public async Task AddToCart_RejectsUnknownProducts_UnfinishedItems_AndDoubleAdds()
    {
        var runId = await SeedFinishedRunAsync(GroceryScout.Core.MatchKind.Exact, GroceryScout.Core.CartStatus.NotAttempted);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync($"/api/runs/{runId}/items/0/add-to-cart", new { provider = "Blinkit", productId = "nope" })).StatusCode);
        // wrong provider for a real product id
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync($"/api/runs/{runId}/items/0/add-to-cart", new { provider = "Amazon", productId = "small" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/runs/{runId}/items/1/add-to-cart", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/runs/{runId}/items/9/add-to-cart", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/runs/99999/items/0/add-to-cart", null)).StatusCode);

        var added = await SeedFinishedRunAsync(GroceryScout.Core.MatchKind.Exact, GroceryScout.Core.CartStatus.Added);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/runs/{added}/items/0/add-to-cart", null)).StatusCode);

        var adding = await SeedFinishedRunAsync(GroceryScout.Core.MatchKind.Exact, GroceryScout.Core.CartStatus.Adding);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/runs/{adding}/items/0/add-to-cart", null)).StatusCode);
    }

    [Fact]
    public async Task CreateRun_StoresPreferPurchasedChoice()
    {
        await _client.PostAsJsonAsync("/api/locations", new { label = "Home", pincode = "560100" });

        var defaultRun = await Json(await _client.PostAsJsonAsync("/api/runs", new { text = "rice 1kg" }));
        var offRun = await Json(await _client.PostAsJsonAsync("/api/runs", new { text = "rice 1kg", preferPurchased = false }));

        var a = await Json(await _client.GetAsync($"/api/runs/{(long)defaultRun["id"]!}"));
        var b = await Json(await _client.GetAsync($"/api/runs/{(long)offRun["id"]!}"));
        Assert.True((bool)a["run"]!["preferPurchased"]!);
        Assert.False((bool)b["run"]!["preferPurchased"]!);
    }

    [Fact]
    public async Task Run_FailsClearlyWhenBrowserIsNotRunning_AndCanBeDeleted()
    {
        await _client.PostAsJsonAsync("/api/locations", new { label = "Home", pincode = "560100" });

        var created = await _client.PostAsJsonAsync("/api/runs", new { text = "turmeric powder 200g\nmilk 1l", addToCart = false });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var id = (long)(await Json(created))["id"]!;

        JsonNode? run = null;
        for (var i = 0; i < 100; i++)
        {
            run = await Json(await _client.GetAsync($"/api/runs/{id}"));
            if ((string)run["run"]!["status"]! is "Failed" or "Completed") break;
            await Task.Delay(100);
        }

        Assert.Equal("Failed", (string)run!["run"]!["status"]!);
        Assert.Contains("start-chrome", (string)run["run"]!["error"]!);
        Assert.Equal("560100", (string)run["run"]!["pincode"]!);
        Assert.Equal(2, run["items"]!.AsArray().Count);
        Assert.Equal("Pending", (string)run["items"]![0]!["status"]!);

        var runs = (await Json(await _client.GetAsync("/api/runs"))).AsArray();
        Assert.Single(runs);

        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/runs/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/runs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/runs/{id}")).StatusCode);
    }

    [Fact]
    public async Task RunUnknown_Returns404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/runs/12345")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/runs/12345/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task ApiKey_IsEnforcedWhenConfigured()
    {
        using var secured = new ApiTests("s3cret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await secured._client.GetAsync("/api/health")).StatusCode);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        req.Headers.Add("X-Api-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await secured._client.SendAsync(req)).StatusCode);

        using var ok = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        ok.Headers.Add("X-Api-Key", "s3cret");
        Assert.Equal(HttpStatusCode.OK, (await secured._client.SendAsync(ok)).StatusCode);
    }
}

public class SelectorConfigMergeTests
{
    [Fact]
    public void Merge_OverridesOnlyGivenProperties_CaseInsensitively()
    {
        var merged = SelectorConfig.Merge(BlinkitAdapter.Defaults, """{"cardTitle":"h1","BaseUrl":"https://example.test/"}""");
        Assert.Equal("h1", merged.CardTitle);
        Assert.Equal("https://example.test/", merged.BaseUrl);
        Assert.Equal(BlinkitAdapter.Defaults.CardPrice, merged.CardPrice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("""{"unknownProperty":"x"}""")]
    public void Merge_IgnoresEmptyOrIrrelevantOverrides(string? json) =>
        Assert.Equal(BlinkitAdapter.Defaults, SelectorConfig.Merge(BlinkitAdapter.Defaults, json));
}
