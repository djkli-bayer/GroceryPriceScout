using GroceryScout.Core;

namespace GroceryScout.Tests;

public class RuleBasedListParserTests
{
    [Fact]
    public void ParsesSizeAndName()
    {
        var r = Assert.Single(RuleBasedListParser.Parse("turmeric powder 200g"));
        Assert.Equal("turmeric powder", r.Item);
        Assert.Equal(200m, r.SizeValue);
        Assert.Equal(SizeUnit.Gram, r.Unit);
        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void ParsesMultipleLinesWithBulletsAndBlankLines()
    {
        var items = RuleBasedListParser.Parse("- milk 500 ml\n\n2) basmati rice 5kg\n* tomatoes");
        Assert.Equal(["milk", "basmati rice", "tomatoes"], items.Select(i => i.Item));
        Assert.Equal(5000m, items[1].SizeValue);
        Assert.Null(items[2].SizeValue);
    }

    [Theory]
    [InlineData("milk 500ml x2", 2, 500)]
    [InlineData("milk 2 x 500ml", 2, 500)]
    [InlineData("milk 500ml (3)", 3, 500)]
    [InlineData("milk 500ml qty 4", 4, 500)]
    public void ParsesCounts(string line, int count, int size)
    {
        var r = Assert.Single(RuleBasedListParser.Parse(line));
        Assert.Equal("milk", r.Item);
        Assert.Equal(count, r.Count);
        Assert.Equal((decimal)size, r.SizeValue);
        Assert.Equal(SizeUnit.Millilitre, r.Unit);
    }

    [Fact]
    public void ParsesBrandPreference()
    {
        var r = Assert.Single(RuleBasedListParser.Parse("turmeric powder 200g brand: Tata Sampann"));
        Assert.Equal("turmeric powder", r.Item);
        Assert.Equal("Tata Sampann", r.BrandPreference);
        Assert.Equal(200m, r.SizeValue);
    }

    [Fact]
    public void SizeOnlySegmentAttachesToPreviousItem()
    {
        var r = Assert.Single(RuleBasedListParser.Parse("salt, 1kg"));
        Assert.Equal("salt", r.Item);
        Assert.Equal(1000m, r.SizeValue);
    }

    [Fact]
    public void CommaSeparatedItems()
    {
        var items = RuleBasedListParser.Parse("milk 1l, eggs 12 pcs; bread");
        Assert.Equal(["milk", "eggs", "bread"], items.Select(i => i.Item));
        Assert.Equal(SizeUnit.Piece, items[1].Unit);
    }

    [Fact]
    public void BareNumberStaysInNameAndDoesNotBecomeCount()
    {
        var r = Assert.Single(RuleBasedListParser.Parse("eggs 12"));
        Assert.Equal("eggs 12", r.Item);
        Assert.Equal(1, r.Count);
    }

    [Theory]
    [InlineData("half a dozen eggs", "eggs", 6)]
    [InlineData("eggs 1 dozen", "eggs", 12)]
    [InlineData("a dozen bananas", "bananas", 12)]
    [InlineData("2 dozen eggs", "eggs", 24)]
    [InlineData("eggs half dozen", "eggs", 6)]
    public void DozensBecomePieceSizes(string line, string item, int pieces)
    {
        var r = Assert.Single(RuleBasedListParser.Parse(line));
        Assert.Equal(item, r.Item);
        Assert.Equal(pieces, r.SizeValue);
        Assert.Equal(SizeUnit.Piece, r.Unit);
        Assert.Equal(1, r.Count);
    }

    [Theory]
    [InlineData("turmeric powder", 200, SizeUnit.Gram, "turmeric powder 200g")]
    [InlineData("rice", 5000, SizeUnit.Gram, "rice 5kg")]
    [InlineData("milk", 1500, SizeUnit.Millilitre, "milk 1500ml")]
    [InlineData("milk", 2000, SizeUnit.Millilitre, "milk 2l")]
    [InlineData("eggs", 12, SizeUnit.Piece, "eggs 12 pcs")]
    public void SearchQuery_FormatsSize(string item, int size, SizeUnit unit, string expected) =>
        Assert.Equal(expected, new ItemRequest(item, size, unit).SearchQuery());
}
