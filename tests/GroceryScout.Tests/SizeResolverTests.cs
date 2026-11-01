using GroceryScout.Core;

namespace GroceryScout.Tests;

public class SizeResolverTests
{
    private static ProductListing L(string p, decimal size, decimal price, bool stock = true) =>
        new(p, "Turmeric", null, size, SizeUnit.Gram, price, null, stock, "u", $"{p}{size}");

    private static readonly ItemRequest Req = new("turmeric powder", 200, SizeUnit.Gram);

    [Fact]
    public void Exact_PicksCheapestAcrossProviders()
    {
        var r = SizeResolver.Resolve(Req, [L("A", 200, 90), L("B", 200, 80), L("C", 500, 10)]);
        Assert.Equal(MatchKind.Exact, r.Kind);
        Assert.Equal("B", r.Chosen!.Provider);
    }

    [Fact]
    public void NoExact_PicksNextLargerSizeGlobally()
    {
        var r = SizeResolver.Resolve(Req, [L("A", 500, 150), L("B", 250, 120), L("C", 250, 110)]);
        Assert.Equal(MatchKind.LargerSize, r.Kind);
        Assert.Equal("C", r.Chosen!.Provider);
        Assert.Equal(250, r.Chosen.SizeValue);
    }

    [Fact]
    public void OnlySmaller_PicksLargestSmaller()
    {
        var r = SizeResolver.Resolve(Req, [L("A", 100, 50), L("B", 150, 70)]);
        Assert.Equal(MatchKind.SmallerSize, r.Kind);
        Assert.Equal(150, r.Chosen!.SizeValue);
    }

    [Fact]
    public void OutOfStockIgnored_NotFound()
    {
        var r = SizeResolver.Resolve(Req, [L("A", 200, 90, false)]);
        Assert.Equal(MatchKind.NotFound, r.Kind);
    }

    [Theory]
    [InlineData("Turmeric Powder 200g", 200, SizeUnit.Gram)]
    [InlineData("Toor Dal 1 kg", 1000, SizeUnit.Gram)]
    [InlineData("Oil 1.5 L", 1500, SizeUnit.Millilitre)]
    [InlineData("Milk 500ml", 500, SizeUnit.Millilitre)]
    public void SizeParser_Normalizes(string text, decimal v, SizeUnit u)
    {
        Assert.True(SizeParser.TryParse(text, out var value, out var unit));
        Assert.Equal(v, value);
        Assert.Equal(u, unit);
    }
}
