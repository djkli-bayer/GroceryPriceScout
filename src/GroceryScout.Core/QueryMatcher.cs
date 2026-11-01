using System.Text.RegularExpressions;

namespace GroceryScout.Core;

/// <summary>
/// Cheap relevance filter: every non-size word of the query must appear in the listing title.
/// Search pages also show unrelated "recommended" products, which must not compete on price.
/// </summary>
public static partial class QueryMatcher
{
    private static readonly HashSet<string> StopWords = ["of", "and", "the", "for", "in", "with", "a"];
    private static readonly HashSet<string> UnitWords = ["g", "gm", "gms", "gram", "grams", "kg", "kgs", "ml", "l", "ltr", "litre", "litres", "liter", "liters", "pc", "pcs", "piece", "pieces", "unit", "units"];

    [GeneratedRegex(@"[\p{L}\p{N}.]+")]
    private static partial Regex TokenRegex();

    public static IReadOnlyList<string> QueryTokens(string item) =>
        Tokenize(item)
            .Where(t => !StopWords.Contains(t) && !UnitWords.Contains(t) && !IsNumberOrSize(t))
            .Select(Stem)
            .Distinct()
            .ToList();

    public static bool IsRelevant(string item, string title)
    {
        var wanted = QueryTokens(item);
        if (wanted.Count == 0) return true;
        var have = Tokenize(title).Select(Stem).ToHashSet();
        return wanted.All(have.Contains);
    }

    // Multi-packs and combos ("Pack of 2", "3+1", "2 x 200g") are priced for several units,
    // so their size/price must not be compared with single items.
    public static bool IsMultiPack(string title) => MultiPackRegex().IsMatch(title);

    [GeneratedRegex(@"\b(combo|bundle|pack\s+of\s+(?:[2-9]|\d{2,})|set\s+of\s+(?:[2-9]|\d{2,}))\b|\b\d+\s*[x×]\s*\d|\b\d\s*\+\s*\d", RegexOptions.IgnoreCase)]
    private static partial Regex MultiPackRegex();

    public static IReadOnlyList<ProductListing> FilterRelevant(string item, IEnumerable<ProductListing> listings)
    {
        var wantsMulti = IsMultiPack(item) || item.Contains("pack", StringComparison.OrdinalIgnoreCase);
        return listings
            .Where(l => IsRelevant(item, $"{l.Brand} {l.Title}") && (wantsMulti || !IsMultiPack(l.Title)))
            .ToList();
    }

    private static IEnumerable<string> Tokenize(string text) =>
        TokenRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value.Trim('.')).Where(t => t.Length > 0);

    // "200g" / "1.5kg" / "500" are size tokens, not product words.
    private static bool IsNumberOrSize(string token) => SizeOrNumberRegex().IsMatch(token);

    [GeneratedRegex(@"^\d+(\.\d+)?([a-z]+)?$")]
    private static partial Regex SizeOrNumberRegex();

    private static string Stem(string token) => token switch
    {
        { Length: > 4 } when token.EndsWith("oes") => token[..^2],
        { Length: > 4 } when token.EndsWith("ies") => token[..^3] + "y",
        { Length: > 3 } when token.EndsWith('s') => token[..^1],
        _ => token,
    };
}
