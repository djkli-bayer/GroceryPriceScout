namespace GroceryScout.Providers;

/// <summary>
/// BigBasket search (verified). The delivery location lives in the browser session, so it must be set there
/// beforehand; <see cref="SetLocationAsync"/> only checks that one is selected. Location/quantity selectors are unverified.
/// </summary>
public sealed class BigBasketAdapter(BrowserSession browser, SelectorConfig? config = null)
    : SelectorDrivenAdapter(browser, config ?? Defaults)
{
    public override string Name => "BigBasket";

    public override async Task SetLocationAsync(string pincode, CancellationToken ct = default)
    {
        var page = await PageAsync();
        await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.DOMContentLoaded);
        var missing = page.Locator("button:has-text('Select Location')").Filter(new() { Visible = true });
        if (await missing.CountAsync() > 0)
            throw new InvalidOperationException("BigBasket has no delivery location; set it in the browser first.");
    }

    public static readonly SelectorConfig Defaults = new()
    {
        BaseUrl = "https://www.bigbasket.com/",
        SearchUrlTemplate = "https://www.bigbasket.com/ps/?q={0}&nc=as",
        LocationButton = "button:has-text('Select Location'), [class*='AddressDropdown'] button, header button:has(svg)",
        LocationInput = "input[placeholder*='area' i], input[placeholder*='pincode' i], input[placeholder*='location' i]",
        LocationSuggestion = "[class*='suggestion' i] li, ul[role='listbox'] li, [class*='LocationList'] li",
        ProductCard = "[class*='SKUDeck___StyledDiv']",
        CardTitle = "h3 h3",
        CardBrand = "[class*='BrandName']",
        // In-stock cards show the pack size in a dropdown; out-of-stock ones show it in a plain label.
        CardSize = "[id^='headlessui-listbox-button'] button span, [class*='PackSelector___StyledLabel']",
        CardPrice = "span",
        CardPricePattern = "^ *₹ *[0-9,.]+ *$",
        CardMrp = "span[class*='Pricing___StyledLabel']",
        CardLink = "a[href*='/pd/']",
        CardAddButton = "button",
        CardAddButtonPattern = "^ *Add( to basket)? *$",
        CardOutOfStockPattern = "out of stock|notify me",
        // Product page: scoped by text to the real Add button (a bare "first button" could be some other control).
        ProductPageAddButton = "button:text-matches('^ *Add( to basket)? *$', 'i')",
        QuantityIncrement = "button[aria-label*='increase' i], button:has-text('+')",
    };
}
