using System.Text.RegularExpressions;

namespace GroceryScout.Core;

public static partial class SizeParser
{
    [GeneratedRegex(@"(?<v>\d+(?:\.\d+)?)\s*(?<u>kgs?|kilograms?|kilos?|grams?|gms?|gm|g|litres?|liters?|ltr|l|ml|pcs?|pieces?|units?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    // "a dozen", "half a dozen", "2 dozen", "1.5 dozen"
    [GeneratedRegex(@"\b(?:(?<half>half\s+(?:a\s+)?)|(?<n>\d+(?:\.\d+)?)\s*|a\s+)(?:dozen|doz)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DozenRegex();

    public static bool TryParse(string text, out decimal value, out SizeUnit unit) =>
        TryExtract(text, out value, out unit, out _);

    /// <summary>Parses the first size in <paramref name="text"/> and returns the text with that size removed.</summary>
    public static bool TryExtract(string text, out decimal value, out SizeUnit unit, out string remainder)
    {
        value = 0; unit = SizeUnit.Piece; remainder = text;

        var dozen = DozenRegex().Match(text);
        if (dozen.Success && !SizeRegex().IsMatch(text))
        {
            var dozens = dozen.Groups["half"].Success ? 0.5m
                : dozen.Groups["n"].Success ? decimal.Parse(dozen.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)
                : 1m;
            value = dozens * 12;
            remainder = text.Remove(dozen.Index, dozen.Length);
            return true;
        }

        var m = SizeRegex().Match(text);
        if (!m.Success) return false;

        var v = decimal.Parse(m.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var u = m.Groups["u"].Value.ToLowerInvariant();
        if (u.StartsWith("kg") || u.StartsWith("kilo")) { value = v * 1000; unit = SizeUnit.Gram; }
        else if (u.StartsWith("g")) { value = v; unit = SizeUnit.Gram; }
        else if (u == "ml") { value = v; unit = SizeUnit.Millilitre; }
        else if (u.StartsWith("l")) { value = v * 1000; unit = SizeUnit.Millilitre; }
        else { value = v; unit = SizeUnit.Piece; }
        remainder = text.Remove(m.Index, m.Length);
        return true;
    }
}
