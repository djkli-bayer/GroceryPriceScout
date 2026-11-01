namespace GroceryScout.Core;

public sealed record CartResult(bool Success, string? Error = null);

public interface IProviderAdapter
{
    string Name { get; }
    Task SetLocationAsync(string pincode, CancellationToken ct = default);
    Task<IReadOnlyList<ProductListing>> SearchAsync(string query, CancellationToken ct = default);
    Task<CartResult> AddToCartAsync(ProductListing listing, int quantity, CancellationToken ct = default);
}

/// <summary>
/// Implemented by providers that misbehave when searched from several tabs at once: the run never has more than
/// this many searches in flight for the provider (across all concurrent item workers).
/// </summary>
public interface ISearchLimit
{
    int MaxConcurrentSearches { get; }
}
