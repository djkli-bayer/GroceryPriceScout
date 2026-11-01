using GroceryScout.Core;

namespace GroceryScout.Tests;

public class QueryMatcherTests
{
    [Theory]
    [InlineData("turmeric powder 200g", "Tata Sampann Turmeric/Haldi Powder With Natural Oils", true)]
    [InlineData("turmeric powder 200g", "Emami Mantra Haldi Powder/Turmeric Powder", true)]
    [InlineData("turmeric powder 200g", "Zoff Red Chilli Powder", false)]
    [InlineData("turmeric powder 200g", "bb Royal Turmeric - Sticks", false)]
    [InlineData("tomatoes 1 kg", "Fresh Tomato - Local", true)]
    [InlineData("basmati rice 5kg", "India Gate Basmati Rice", true)]
    [InlineData("basmati rice 5kg", "Sona Masoori Rice", false)]
    public void IsRelevant_RequiresAllNonSizeWords(string item, string title, bool expected) =>
        Assert.Equal(expected, QueryMatcher.IsRelevant(item, title));

    [Fact]
    public void QueryTokens_DropSizeAndUnitWords()
    {
        Assert.Equal(["turmeric", "powder"], QueryMatcher.QueryTokens("turmeric powder 200 g"));
        Assert.Equal(["turmeric", "powder"], QueryMatcher.QueryTokens("Turmeric Powder 200g"));
    }

    [Fact]
    public void SizeOnlyQuery_MatchesEverything() =>
        Assert.True(QueryMatcher.IsRelevant("500g", "Anything"));

    [Theory]
    [InlineData("Kalprishi Haldi Powder 200g (Pack of 2) | Pure", true)]
    [InlineData("RHB's Desi Zaika 3+1 Combo Pack 600g", true)]
    [InlineData("Everest Masala 2 x 100g", true)]
    [InlineData("Aashirvaad Powder - Turmeric, 200 Grams Pouch", false)]
    [InlineData("Catch Turmeric Powder, 200g", false)]
    [InlineData("Tata Sampann Pack of 1 Turmeric 200g", false)]
    public void IsMultiPack_DetectsBundles(string title, bool expected) =>
        Assert.Equal(expected, QueryMatcher.IsMultiPack(title));

    [Fact]
    public void FilterRelevant_DropsMultiPacksUnlessRequested()
    {
        ProductListing P(string t) => new("A", t, null, 200, SizeUnit.Gram, 50, null, true, "u", t);
        var listings = new[] { P("Turmeric Powder 200g"), P("Turmeric Powder 200g (Pack of 2)") };

        Assert.Single(QueryMatcher.FilterRelevant("turmeric powder 200g", listings));
        var packOnly = QueryMatcher.FilterRelevant("turmeric powder pack of 2", listings);
        Assert.Equal("Turmeric Powder 200g (Pack of 2)", Assert.Single(packOnly).Title);
    }
}
