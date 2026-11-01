using GroceryScout.Core;
using GroceryScout.Data;
using GroceryScout.Providers;

namespace GroceryScout.Api;

/// <summary>Builds adapters for the enabled providers. New providers only need an entry here.</summary>
public static class ProviderFactory
{
    private static readonly Dictionary<string, Func<BrowserSession, SelectorConfig?, IProviderAdapter>> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["BigBasket"] = (b, c) => new BigBasketAdapter(b, c),
            ["Amazon"] = (b, c) => new AmazonAdapter(b, c),
            ["Blinkit"] = (b, c) => new BlinkitAdapter(b, c),
        };

    private static readonly Dictionary<string, SelectorConfig> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BigBasket"] = BigBasketAdapter.Defaults,
        ["Amazon"] = AmazonAdapter.Defaults,
        ["Blinkit"] = BlinkitAdapter.Defaults,
    };

    public static IReadOnlyList<IProviderAdapter> Create(BrowserSession browser, IEnumerable<ProviderSetting> settings) =>
        settings
            .Where(s => s.Enabled && Known.ContainsKey(s.Name))
            .Select(s => Build(browser, s))
            .ToList();

    /// <summary>One adapter by name, even if the provider has since been disabled (e.g. adding to a cart after a run).</summary>
    public static IProviderAdapter? CreateFor(BrowserSession browser, IEnumerable<ProviderSetting> settings, string name) =>
        settings.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } s && Known.ContainsKey(s.Name)
            ? Build(browser, s)
            : null;

    private static IProviderAdapter Build(BrowserSession browser, ProviderSetting s) =>
        Known[s.Name](browser, SelectorConfig.Merge(Defaults[s.Name], s.SelectorConfigJson));
}
