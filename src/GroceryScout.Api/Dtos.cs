using GroceryScout.Core;

namespace GroceryScout.Api;

public sealed record ItemDto(string Item, decimal? SizeValue, SizeUnit? Unit, int Count = 1, string? Brand = null)
{
    public ItemRequest ToRequest() => new(Item.Trim(), SizeValue, SizeValue is null ? null : Unit, Count, string.IsNullOrWhiteSpace(Brand) ? null : Brand.Trim());

    public static ItemDto From(ItemRequest r) => new(r.Item, r.SizeValue, r.Unit, r.Count, r.BrandPreference);
}

public sealed record ParseListRequest(string? Text);

public sealed record CreateRunRequest(string? Text, List<ItemDto>? Items, string? Pincode, bool AddToCart = false, bool PreferPurchased = true);

public sealed record AddLocationRequest(string? Label, string? Pincode, bool MakeDefault = false);

public sealed record SetEnabledRequest(bool Enabled);

public sealed record SetSelectorsRequest(string? SelectorConfigJson);

/// <summary>Which option to add; omitted fields mean "the one the app chose".</summary>
public sealed record AddToCartRequest(string? Provider, string? ProductId);

public static class Validation
{
    public const int MaxItems = 200;
    public const int MaxUnreviewedCount = 5;

    public static bool IsPincode(string? s) => s is { Length: 6 } && s.All(char.IsAsciiDigit);

    /// <summary>Returns an error message, or null when the items are acceptable.</summary>
    public static string? CheckItems(IReadOnlyList<ItemRequest> items)
    {
        if (items.Count == 0) return "No items found in the list.";
        if (items.Count > MaxItems) return $"At most {MaxItems} items per run.";
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (string.IsNullOrWhiteSpace(it.Item)) return $"Item {i + 1} has no name.";
            if (it.Item.Length > 120) return $"Item {i + 1} name is too long.";
            if (it.Count is < 1 or > 99) return $"Item {i + 1} count must be 1-99.";
            if (it.SizeValue is <= 0) return $"Item {i + 1} size must be positive.";
        }
        return null;
    }
}
