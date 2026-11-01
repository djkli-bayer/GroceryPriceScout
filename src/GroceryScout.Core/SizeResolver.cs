namespace GroceryScout.Core;

/// <summary>
/// Picks the size across all providers: exact, else next larger, else largest smaller.
/// Within the chosen size the order of preference is: a product the user has bought before (when
/// <c>preferPurchased</c>), then a favourite brand costing at most <c>brandTolerance</c> more than the
/// cheapest candidate, then simply the cheapest in-stock listing.
/// </summary>
public static class SizeResolver
{
    public static Resolution Resolve(
        ItemRequest request, IEnumerable<ProductListing> listings, decimal brandTolerance = 0.10m, bool preferPurchased = true)
    {
        var pool = listings.Where(l => l.InStock && (request.Unit is null || l.Unit == request.Unit)).ToList();
        if (pool.Count == 0)
            return new Resolution(MatchKind.NotFound, null, pool);
        if (request.SizeValue is null)
            return new Resolution(MatchKind.Exact, Pick(pool, request, brandTolerance, preferPurchased), pool);

        var target = request.SizeValue.Value;

        var exact = pool.Where(l => l.SizeValue == target).ToList();
        if (exact.Count > 0) return new Resolution(MatchKind.Exact, Pick(exact, request, brandTolerance, preferPurchased), exact);

        var larger = pool.Where(l => l.SizeValue > target).ToList();
        if (larger.Count > 0)
        {
            var next = larger.Min(l => l.SizeValue);
            var set = larger.Where(l => l.SizeValue == next).ToList();
            return new Resolution(MatchKind.LargerSize, Pick(set, request, brandTolerance, preferPurchased), set);
        }

        var smaller = pool.Where(l => l.SizeValue < target).ToList();
        if (smaller.Count == 0)
            return new Resolution(MatchKind.NotFound, null, pool);
        var best = smaller.Max(l => l.SizeValue);
        var smallSet = smaller.Where(l => l.SizeValue == best).ToList();
        return new Resolution(MatchKind.SmallerSize, Pick(smallSet, request, brandTolerance, preferPurchased), smallSet);
    }

    private static ProductListing Pick(IReadOnlyCollection<ProductListing> set, ItemRequest request, decimal brandTolerance, bool preferPurchased)
    {
        // A purchased product always wins, however much more it costs; only the others are compared on price.
        if (preferPurchased && set.Any(l => l.Purchased))
            set = set.Where(l => l.Purchased).ToList();

        var cheapest = set.OrderBy(x => x.Price).First();
        if (string.IsNullOrWhiteSpace(request.BrandPreference)) return cheapest;

        var limit = cheapest.Price * (1 + brandTolerance);
        var preferred = set
            .Where(l => l.Price <= limit && MatchesBrand(l, request.BrandPreference))
            .OrderBy(l => l.Price)
            .FirstOrDefault();
        return preferred ?? cheapest;
    }

    private static bool MatchesBrand(ProductListing l, string brand) =>
        (l.Brand ?? "").Contains(brand, StringComparison.OrdinalIgnoreCase) ||
        l.Title.Contains(brand, StringComparison.OrdinalIgnoreCase);
}
