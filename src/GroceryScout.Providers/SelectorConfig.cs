using System.Text.Json;
using System.Text.Json.Nodes;

namespace GroceryScout.Providers;

/// <summary>
/// How to read a provider's search results. Card fields use plain CSS selectors (run inside the page, so a whole
/// results page is read in one browser call); where a text test is needed, a regular expression pattern
/// (case-insensitive) is applied to the matched element's text, e.g. the first <c>span</c> that looks like a price.
/// </summary>
public sealed record SelectorConfig
{
    public string BaseUrl { get; init; } = "";
    public string SearchUrlTemplate { get; init; } = "";          // {0} = url-encoded query
    public string LocationButton { get; init; } = "";
    public string LocationInput { get; init; } = "";
    public string LocationSuggestion { get; init; } = "";
    public string ProductCard { get; init; } = "";
    public string CardTitle { get; init; } = "";
    public string CardBrand { get; init; } = "";
    public string CardSize { get; init; } = "";
    public string CardPrice { get; init; } = "";
    public string CardPricePattern { get; init; } = "";             // only elements whose text matches count as the price
    public string CardMrp { get; init; } = "";
    public string CardLink { get; init; } = "";

    // An in-stock card has an "add" control: CardAddButton (+ optional text pattern) or CardInCartSelector
    // (shown instead once the item is in the cart). Leave CardAddButton empty to treat every card as purchasable.
    public string CardAddButton { get; init; } = "";
    public string CardAddButtonPattern { get; init; } = "";
    public string CardInCartSelector { get; init; } = "";

    // Regex tested against the whole card's text, e.g. "out of stock|notify me".
    public string CardOutOfStockPattern { get; init; } = "";
    public string QuantityIncrement { get; init; } = "";

    // Optional: element whose text marks a product the user has bought before (e.g. Amazon's "Purchased Jan 2025").
    public string CardPurchasedBadge { get; init; } = "";

    // Optional: providers whose cards carry a stable id attribute (e.g. Amazon's data-asin).
    // When set, the product id is read from that attribute and the url is built from ProductUrlTemplate.
    public string ProductIdAttribute { get; init; } = "";
    public string ProductUrlTemplate { get; init; } = "";               // {0} = product id

    // Optional: add-to-cart controls on the product page when they differ from the card's.
    public string ProductPageAddButton { get; init; } = "";
    public string ProductPageQuantitySelect { get; init; } = "";        // <select>; chosen by value = quantity

    public static SelectorConfig LoadOrDefault(string? path, SelectorConfig fallback)
    {
        if (path is null || !File.Exists(path)) return fallback;
        return JsonSerializer.Deserialize<SelectorConfig>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? fallback;
    }

    /// <summary>
    /// Applies a partial JSON override (only the properties you want to change, any casing) on top of
    /// <paramref name="defaults"/>. Lets a broken selector be fixed from settings without a redeploy.
    /// </summary>
    public static SelectorConfig Merge(SelectorConfig defaults, string? overrideJson)
    {
        if (string.IsNullOrWhiteSpace(overrideJson)) return defaults;
        if (JsonNode.Parse(overrideJson) is not JsonObject overrides) return defaults;

        var merged = JsonSerializer.SerializeToNode(defaults)!.AsObject();
        foreach (var (key, value) in overrides)
        {
            var existing = merged.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) merged[existing] = value?.DeepClone();
        }
        return merged.Deserialize<SelectorConfig>() ?? defaults;
    }
}
