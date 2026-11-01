using GroceryScout.Core;
using GroceryScout.Providers;

namespace GroceryScout.Tests;

public class PurchasedPriorityTests
{
    private static ProductListing L(string provider, string id, decimal size, decimal price, bool purchased = false, string? brand = null) =>
        new(provider, $"{brand} Turmeric Powder", brand, size, SizeUnit.Gram, price, null, true, $"https://x/{id}", id,
            Purchased: purchased, PurchasedNote: purchased ? "Jan 2025" : null);

    private static readonly ItemRequest Req = new("turmeric powder", 200, SizeUnit.Gram);

    [Fact]
    public void PurchasedWinsEvenWhenMuchMoreExpensive()
    {
        var r = SizeResolver.Resolve(Req, [L("BigBasket", "a", 200, 51.6m), L("Amazon", "b", 200, 94, purchased: true)]);

        Assert.Equal(MatchKind.Exact, r.Kind);
        Assert.Equal("b", r.Chosen!.ProductId);
        Assert.True(r.Chosen.Purchased);
    }

    [Fact]
    public void CheapestPurchasedWinsAmongSeveralPurchased()
    {
        var r = SizeResolver.Resolve(Req,
            [L("Amazon", "p1", 200, 120, purchased: true), L("Amazon", "p2", 200, 94, purchased: true), L("Blinkit", "c", 200, 50)]);
        Assert.Equal("p2", r.Chosen!.ProductId);
    }

    [Fact]
    public void PurchasedOnlyCountsWithinTheChosenSize()
    {
        // The exact size exists (not purchased); a purchased 500 g pack must not override the size rule.
        var r = SizeResolver.Resolve(Req, [L("A", "exact", 200, 80), L("Amazon", "big", 500, 90, purchased: true)]);
        Assert.Equal("exact", r.Chosen!.ProductId);
    }

    [Fact]
    public void PurchasedAppliesWithinNextLargerSize_WhenNoExactSize()
    {
        var r = SizeResolver.Resolve(Req, [L("A", "cheap", 250, 70), L("Amazon", "bought", 250, 99, purchased: true), L("B", "huge", 500, 10)]);
        Assert.Equal(MatchKind.LargerSize, r.Kind);
        Assert.Equal("bought", r.Chosen!.ProductId);
    }

    [Fact]
    public void FavouriteBrandIsAppliedWithinPurchasedItems_NotOverPurchased()
    {
        var req = Req with { BrandPreference = "Tata" };
        var listings = new[]
        {
            L("A", "tata-notbought", 200, 60, brand: "Tata"),
            L("Amazon", "everest-bought", 200, 94, purchased: true, brand: "Everest"),
            L("Amazon", "tata-bought", 200, 100, purchased: true, brand: "Tata"),
        };
        // Purchased first: both purchased items qualify; among them the favourite brand is within 10% of the cheapest purchased (94 -> 103.4).
        Assert.Equal("tata-bought", SizeResolver.Resolve(req, listings).Chosen!.ProductId);

        // Without any purchased item, the brand rule works as before.
        Assert.Equal("tata-notbought", SizeResolver.Resolve(req, [listings[0], L("B", "x", 200, 59, brand: "Other")]).Chosen!.ProductId);
    }

    [Fact]
    public void CanBeSwitchedOff_FallingBackToCheapest()
    {
        var r = SizeResolver.Resolve(Req, [L("BigBasket", "a", 200, 51.6m), L("Amazon", "b", 200, 94, purchased: true)], preferPurchased: false);
        Assert.Equal("a", r.Chosen!.ProductId);
    }

    [Fact]
    public void OutOfStockPurchasedItemIsIgnored()
    {
        var oos = L("Amazon", "b", 200, 94, purchased: true) with { InStock = false };
        var r = SizeResolver.Resolve(Req, [L("BigBasket", "a", 200, 51.6m), oos]);
        Assert.Equal("a", r.Chosen!.ProductId);
    }

    [Theory]
    [InlineData("Purchased Jan 2025", "Jan 2025")]
    [InlineData("Purchased 1 time", "1 time")]
    [InlineData("  purchased Apr 2026 ", "Apr 2026")]
    [InlineData("Purchased", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PurchasedNote_StripsThePrefix(string? badge, string? expected) =>
        Assert.Equal(expected, SelectorDrivenAdapter.PurchasedNote(badge));

    [Fact]
    public void AmazonDefaults_DetectThePurchasedBadge() =>
        Assert.Contains("past-purchased", AmazonAdapter.Defaults.CardPurchasedBadge);

    [Fact]
    public void OldStoredListingsWithoutPurchasedFieldsStillDeserialize()
    {
        const string json = """{"provider":"A","title":"t","brand":null,"sizeValue":200,"unit":"Gram","price":5,"mrp":null,"inStock":true,"url":"u","productId":"p"}""";
        var l = System.Text.Json.JsonSerializer.Deserialize<ProductListing>(json, GroceryScout.Data.Store.Json)!;
        Assert.False(l.Purchased);
        Assert.Null(l.PurchasedNote);
    }
}
