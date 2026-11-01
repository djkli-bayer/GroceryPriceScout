using GroceryScout.Core;
using GroceryScout.Providers;
using Microsoft.Playwright;

namespace GroceryScout.Tests;

/// <summary>
/// Runs the real in-page extraction script and the listing builder against saved real card HTML
/// (Fixtures/*.html, captured from the live sites with session tokens stripped). If a store changes its markup,
/// recapture the fixture with <c>SpikeRunner dump &lt;url&gt; --selector=&lt;card&gt; --sanitize</c> and update the selectors.
/// The tests need Playwright's Chromium (see README); without it they are skipped silently.
/// </summary>
public sealed class CardExtractionTests : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        }
        catch (PlaywrightException)
        {
            _browser = null; // Chromium is not installed on this machine
        }
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    private async Task<CardData?> ExtractAsync(string fixture, SelectorConfig config)
    {
        if (_browser is null) return null;
        var html = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        var page = await _browser.NewPageAsync();
        try
        {
            await page.RouteAsync("**/*", r => r.AbortAsync()); // no network: only the markup matters
            await page.SetContentAsync($"<html><body>{html}</body></html>");
            return Assert.Single(await CardExtractor.ExtractAsync(page, config));
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private static ProductListing Build(CardData card, SelectorConfig config, string provider, string query)
    {
        var result = ListingBuilder.Build(card, config, provider, query);
        Assert.True(result.Rendered);
        return Assert.IsType<ProductListing>(result.Listing);
    }

    // ---- BigBasket ------------------------------------------------------------------------------

    [Fact]
    public async Task BigBasket_InStockCard()
    {
        var config = BigBasketAdapter.Defaults;
        if (await ExtractAsync("bigbasket-instock.html", config) is not { } card) return;

        var l = Build(card, config, "BigBasket", "turmeric powder 200g");
        Assert.Equal("Tata Sampann Turmeric/Haldi Powder With Natural Oils", l.Title);
        Assert.Equal("Tata Sampann", l.Brand);
        Assert.Equal(200m, l.SizeValue);
        Assert.Equal(SizeUnit.Gram, l.Unit);
        Assert.Equal(75.46m, l.Price);
        Assert.Equal(98m, l.Mrp);
        Assert.True(l.InStock);
        Assert.False(l.Purchased);
        Assert.Equal("40070769", l.ProductId);
        Assert.StartsWith("https://www.bigbasket.com/pd/40070769/", l.Url);
    }

    [Fact]
    public async Task BigBasket_NotifyMeCardIsOutOfStock()
    {
        var config = BigBasketAdapter.Defaults;
        if (await ExtractAsync("bigbasket-outofstock.html", config) is not { } card) return;

        var l = Build(card, config, "BigBasket", "turmeric powder 200g");
        Assert.Equal(90m, l.Price);
        Assert.Equal(200m, l.SizeValue);
        Assert.False(l.InStock);
        Assert.Equal("40366424", l.ProductId);
    }

    // ---- Blinkit --------------------------------------------------------------------------------

    [Fact]
    public async Task Blinkit_InStockCard()
    {
        var config = BlinkitAdapter.Defaults;
        if (await ExtractAsync("blinkit-instock.html", config) is not { } card) return;

        var l = Build(card, config, "Blinkit", "turmeric powder 200g");
        Assert.Equal("Aashirvaad Haldi/ Turmeric Powder (Arshina Pudi)", l.Title);
        Assert.Equal(200m, l.SizeValue);
        Assert.Equal(79m, l.Price);
        Assert.Equal(100m, l.Mrp);
        Assert.True(l.InStock);
        Assert.Equal("69381", l.ProductId);
        Assert.Equal("https://blinkit.com/prn/product/prid/69381", l.Url);
    }

    [Fact]
    public async Task Blinkit_CardAlreadyInCartStaysPurchasable()
    {
        var config = BlinkitAdapter.Defaults;
        if (await ExtractAsync("blinkit-incart.html", config) is not { } card) return;

        Assert.True(card.HasAdd); // the +/- stepper replaces the ADD button
        var l = Build(card, config, "Blinkit", "turmeric powder 200g");
        Assert.Equal(53m, l.Price);
        Assert.True(l.InStock);
        Assert.Equal("507104", l.ProductId);
    }

    // ---- Amazon ---------------------------------------------------------------------------------

    [Fact]
    public async Task Amazon_NormalCard_UsesMainPriceNotUnitPrice()
    {
        var config = AmazonAdapter.Defaults;
        if (await ExtractAsync("amazon-normal.html", config) is not { } card) return;

        var l = Build(card, config, "Amazon", "turmeric powder 200g");
        Assert.Equal("Catch Turmeric Powder, 200g", l.Title);
        Assert.Equal(56m, l.Price); // not the ₹28 per-100 g unit price
        Assert.Equal(86m, l.Mrp);
        Assert.Equal(200m, l.SizeValue);
        Assert.True(l.InStock);
        Assert.False(l.Purchased);
        Assert.Equal("B0154J83IC", l.ProductId);
        Assert.Equal("https://www.amazon.in/dp/B0154J83IC", l.Url);
    }

    [Fact]
    public async Task Amazon_PurchasedBadgeIsDetected()
    {
        var config = AmazonAdapter.Defaults;
        if (await ExtractAsync("amazon-purchased.html", config) is not { } card) return;

        var l = Build(card, config, "Amazon", "milk 500ml");
        Assert.Equal("Nandini Pasteurised Toned Milk, 500ml Pack", l.Title);
        Assert.Equal(24m, l.Price);
        Assert.Equal(500m, l.SizeValue);
        Assert.Equal(SizeUnit.Millilitre, l.Unit);
        Assert.True(l.Purchased);
        Assert.Equal("Apr 2026", l.PurchasedNote);
        Assert.Equal("B07FXRZ7M5", l.ProductId);
    }

    // ---- shared behaviour -----------------------------------------------------------------------

    [Fact]
    public async Task IrrelevantCardsAreSkippedForGood()
    {
        var config = BlinkitAdapter.Defaults;
        if (await ExtractAsync("blinkit-instock.html", config) is not { } card) return;

        var result = ListingBuilder.Build(card, config, "Blinkit", "basmati rice 1kg");
        Assert.True(result.Rendered); // handled: never needs re-reading
        Assert.Null(result.Listing);
    }

    [Fact]
    public void EmptyCardIsRetriedLater()
    {
        var empty = new CardData(null, null, null, null, null, null, null, null, null, true, false);
        var result = ListingBuilder.Build(empty, BigBasketAdapter.Defaults, "BigBasket", "rice");
        Assert.False(result.Rendered);
        Assert.Null(result.Listing);
        Assert.Null(ListingBuilder.KeyOf(empty, BigBasketAdapter.Defaults));
    }

    [Fact]
    public void UnavailableCardWithoutPriceIsHandled_ButOneStillLoadingIsRetried()
    {
        var config = AmazonAdapter.Defaults;
        var unavailable = new CardData("B0X", null, "Vedaka Turmeric Powder 200g", null, null, null, null, null, null, true, OutOfStock: true);
        var r = ListingBuilder.Build(unavailable, config, "Amazon", "turmeric powder 200g");
        Assert.True(r.Rendered);
        Assert.Null(r.Listing);

        var loading = unavailable with { OutOfStock = false };
        Assert.False(ListingBuilder.Build(loading, config, "Amazon", "turmeric powder 200g").Rendered);
    }

    [Fact]
    public void CardWithTitleButNoPriceOrSizeIsRetriedLater()
    {
        var config = BigBasketAdapter.Defaults;
        var noPrice = new CardData(null, "/pd/1/x/", "Rice", null, null, "1 kg", null, null, null, true, false);
        Assert.False(ListingBuilder.Build(noPrice, config, "BigBasket", "rice").Rendered);

        var noSize = new CardData(null, "/pd/1/x/", "Rice", null, "₹90", null, null, null, null, true, false);
        Assert.False(ListingBuilder.Build(noSize, config, "BigBasket", "rice").Rendered);
    }

    [Fact]
    public void SizeFallsBackToTitleThenImageAlt()
    {
        var config = BigBasketAdapter.Defaults;
        var fromTitle = new CardData(null, "/pd/1/x/", "Basmati Rice 5 kg", null, "₹500", null, null, null, null, true, false);
        Assert.Equal(5000m, ListingBuilder.Build(fromTitle, config, "BigBasket", "rice").Listing!.SizeValue);

        var fromAlt = new CardData(null, "/pd/2/x/", "Basmati Rice", null, "₹500", null, null, "Basmati Rice 1 kg", null, true, false);
        Assert.Equal(1000m, ListingBuilder.Build(fromAlt, config, "BigBasket", "rice").Listing!.SizeValue);
    }
}
