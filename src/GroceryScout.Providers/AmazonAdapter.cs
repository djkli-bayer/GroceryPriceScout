namespace GroceryScout.Providers;

/// <summary>
/// Regular Amazon.in grocery search (Amazon Now/Fresh is not available on the web for every address).
/// Uses the account's saved delivery address, so the location must be set in the browser beforehand.
/// </summary>
public sealed class AmazonAdapter(BrowserSession browser, SelectorConfig? config = null)
    : SelectorDrivenAdapter(browser, config ?? Defaults)
{
    public override string Name => "Amazon";

    public static readonly SelectorConfig Defaults = new()
    {
        BaseUrl = "https://www.amazon.in/",
        SearchUrlTemplate = "https://www.amazon.in/s?k={0}&i=grocery",
        ProductCard = "div[data-component-type='s-search-result'][data-asin]",
        ProductIdAttribute = "data-asin",
        ProductUrlTemplate = "https://www.amazon.in/dp/{0}",
        CardTitle = "h2 span",
        // The unit price (per 100 g) is also an .a-price, but carries .a-text-price; the struck-through MRP does too.
        CardPrice = "span.a-price:not(.a-text-price) > span.a-offscreen",
        CardMrp = "span.a-price[data-a-strike='true'] > span.a-offscreen",
        CardOutOfStockPattern = "currently unavailable",
        // Amazon marks products bought before with <span id="{asin}-past-purchased"> ("Purchased Jan 2025", "Purchased 1 time").
        CardPurchasedBadge = "span[id$='-past-purchased'] .a-badge-text",
        ProductPageAddButton = "#add-to-cart-button",
        ProductPageQuantitySelect = "#quantity",
    };

    public override async Task SetLocationAsync(string pincode, CancellationToken ct = default)
    {
        var page = await PageAsync();
        var text = (await page.Locator("#glow-ingress-line2").First.InnerTextAsync(new() { Timeout = 10_000 })).Trim();
        if (!text.Contains(pincode))
            throw new InvalidOperationException($"Amazon delivery location is '{text}'; set {pincode} in the browser first.");
    }
}
