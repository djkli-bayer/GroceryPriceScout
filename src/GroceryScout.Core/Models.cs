namespace GroceryScout.Core;

public enum SizeUnit { Gram, Millilitre, Piece }

public sealed record ItemRequest(string Item, decimal? SizeValue, SizeUnit? Unit, int Count = 1, string? BrandPreference = null);

public sealed record ProductListing(
    string Provider,
    string Title,
    string? Brand,
    decimal SizeValue,
    SizeUnit Unit,
    decimal Price,
    decimal? Mrp,
    bool InStock,
    string Url,
    string ProductId,
    // Set when the store itself marks the product as bought before (e.g. Amazon's "Purchased Jan 2025").
    bool Purchased = false,
    string? PurchasedNote = null);

public enum MatchKind { Exact, LargerSize, SmallerSize, NotFound }

public sealed record Resolution(MatchKind Kind, ProductListing? Chosen, IReadOnlyList<ProductListing> Candidates);
