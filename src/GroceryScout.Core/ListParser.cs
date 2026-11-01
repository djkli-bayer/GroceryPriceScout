using System.Globalization;
using System.Text.RegularExpressions;

namespace GroceryScout.Core;

public interface IListParser
{
    Task<IReadOnlyList<ItemRequest>> ParseAsync(string text, CancellationToken ct = default);
}

public static class ItemRequestExtensions
{
    /// <summary>Text sent to provider search boxes, e.g. "turmeric powder 200g".</summary>
    public static string SearchQuery(this ItemRequest r) =>
        r.SizeValue is null ? r.Item : $"{r.Item} {FormatSize(r.SizeValue.Value, r.Unit ?? SizeUnit.Piece)}";

    public static string FormatSize(decimal value, SizeUnit unit)
    {
        string N(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);
        return unit switch
        {
            SizeUnit.Gram when value >= 1000 && value % 1000 == 0 => $"{N(value / 1000)}kg",
            SizeUnit.Gram => $"{N(value)}g",
            SizeUnit.Millilitre when value >= 1000 && value % 1000 == 0 => $"{N(value / 1000)}l",
            SizeUnit.Millilitre => $"{N(value)}ml",
            _ => $"{N(value)} pcs",
        };
    }
}

/// <summary>
/// Offline parser for a pasted grocery list: one item per line (or separated by ';' / ',').
/// Understands sizes ("200g", "1.5 kg"), counts ("x2", "2 x 500ml") and "brand: Tata".
/// A bare number with no unit stays part of the item name (use "x2" for quantity) to avoid buying 12 packs of eggs.
/// </summary>
public sealed partial class RuleBasedListParser : IListParser
{
    public Task<IReadOnlyList<ItemRequest>> ParseAsync(string text, CancellationToken ct = default) =>
        Task.FromResult(Parse(text));

    public static IReadOnlyList<ItemRequest> Parse(string text)
    {
        var segments = new List<string>();
        foreach (var raw in SplitRegex().Split(text))
        {
            var seg = BulletRegex().Replace(raw, "").Trim();
            if (seg.Length == 0) continue;

            // "salt, 1kg": a segment that is only a size/count belongs to the previous item.
            if (segments.Count > 0 && IsSizeOnly(seg)) segments[^1] += " " + seg;
            else segments.Add(seg);
        }

        return segments.Select(ParseLine).Where(r => r.Item.Length > 0).ToList();
    }

    private static bool IsSizeOnly(string seg) =>
        SizeParser.TryExtract(seg, out _, out _, out var rest) && !rest.Any(char.IsLetter);

    private static ItemRequest ParseLine(string line)
    {
        var count = 1;
        string? brand = null;

        var b = BrandRegex().Match(line);
        if (b.Success)
        {
            brand = b.Groups["b"].Value.Trim();
            line = line.Remove(b.Index, b.Length);
        }

        // "2 x 500ml" (count before size) or "500ml x 2" / "x2" (count after).
        var lead = LeadingCountRegex().Match(line);
        if (lead.Success)
        {
            count = int.Parse(lead.Groups["n"].Value, CultureInfo.InvariantCulture);
            line = line.Remove(lead.Index, lead.Length);
        }
        else
        {
            var trail = TrailingCountRegex().Match(line);
            if (trail.Success)
            {
                count = int.Parse(trail.Groups["n"].Value, CultureInfo.InvariantCulture);
                line = line.Remove(trail.Index, trail.Length);
            }
        }

        decimal? size = null;
        SizeUnit? unit = null;
        if (SizeParser.TryExtract(line, out var v, out var u, out var rest))
        {
            size = v; unit = u; line = rest;
        }

        var item = WhitespaceRegex().Replace(line.Trim(' ', '-', ',', ':', '(', ')'), " ");
        return new ItemRequest(item, size, unit, Math.Clamp(count, 1, 99), string.IsNullOrWhiteSpace(brand) ? null : brand);
    }

    [GeneratedRegex(@"[\r\n;,]+")]
    private static partial Regex SplitRegex();

    [GeneratedRegex(@"^\s*(?:[-*•]+|\d+[.)])\s+")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"\b(?:brand|bnd)\s*[:=]\s*(?<b>[\p{L}\p{N}&'. -]+?)\s*(?=$|\(|\bx\s*\d|\d+\s*[x×])", RegexOptions.IgnoreCase)]
    private static partial Regex BrandRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}.])(?<n>\d{1,2})\s*[x×]\s*(?=\d)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingCountRegex();

    [GeneratedRegex(@"\s*[x×]\s*(?<n>\d{1,2})\b|\(\s*(?<n>\d{1,2})\s*\)\s*$|\bqty\s*[:=]?\s*(?<n>\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingCountRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
