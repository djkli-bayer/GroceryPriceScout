namespace GroceryScout.Providers;

/// <summary>
/// Blinkit search. The delivery location lives in the browser session, so it must be set there beforehand.
/// Search-card selectors are verified; the product-page add button is unverified.
/// </summary>
public sealed class BlinkitAdapter(BrowserSession browser, SelectorConfig? config = null)
    : SelectorDrivenAdapter(browser, config ?? Defaults)
{
    public override string Name => "Blinkit";

    // Searching Blinkit from several tabs at once intermittently returned empty pages.
    public override int MaxConcurrentSearches => 1;

    // Blinkit lists relevant products first and then thousands of unrelated recommendations.
    protected override int StaleRoundsLimit => 6;

    public static readonly SelectorConfig Defaults = new()
    {
        BaseUrl = "https://blinkit.com/",
        SearchUrlTemplate = "https://blinkit.com/s/?q={0}",
        // Product cards are role=button divs whose id is the product id; the nested ADD control has no id.
        ProductCard = "div[role='button'][id]:has(img)",
        ProductIdAttribute = "id",
        ProductUrlTemplate = "https://blinkit.com/prn/product/prid/{0}",
        CardTitle = "div[class*='tw-line-clamp-2']",
        CardSize = "div[class*='tw-line-clamp-1']",
        CardPrice = "div",
        CardPricePattern = "^ *₹ *[0-9,.]+ *$",
        CardMrp = "div[class*='tw-line-through']",
        // An item already in the cart shows a +/- stepper instead of ADD; it is still purchasable.
        CardAddButton = "div",
        CardAddButtonPattern = "^ *ADD *$",
        CardInCartSelector = "span.icon-plus",
        CardOutOfStockPattern = "out of stock|notify me",
        // The product page's main button reads "Add to cart"; plain "ADD" belongs to the Similar-products cards below it.
        ProductPageAddButton = "text='Add to cart'",
    };

    public override async Task SetLocationAsync(string pincode, CancellationToken ct = default)
    {
        var page = await PageAsync();
        if (await page.GetByText("Please provide your delivery location").CountAsync() > 0)
            throw new InvalidOperationException("Blinkit has no delivery location; set it in the browser first.");
    }
}
