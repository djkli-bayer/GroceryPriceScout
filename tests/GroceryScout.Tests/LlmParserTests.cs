using GroceryScout.Core;
using GroceryScout.Llm;

namespace GroceryScout.Tests;

public class LlmParserTests
{
    [Fact]
    public void ParseResponse_ConvertsSizesWithSizeParser()
    {
        var items = OllamaListParser.ParseResponse("""
            {"items":[
              {"item":"turmeric powder","size":"200 g","count":1,"brand":null},
              {"item":"basmati rice","size":"5 kg","count":2,"brand":"India Gate"},
              {"item":"tomatoes","size":null,"count":1,"brand":"null"}
            ]}
            """);

        Assert.Equal(3, items.Count);
        Assert.Equal(new ItemRequest("turmeric powder", 200, SizeUnit.Gram), items[0]);
        Assert.Equal(new ItemRequest("basmati rice", 5000, SizeUnit.Gram, 2, "India Gate"), items[1]);
        Assert.Null(items[2].SizeValue);
        Assert.Null(items[2].BrandPreference);
    }

    [Fact]
    public void ParseResponse_ToleratesMissingFieldsAndClampsCount()
    {
        var items = OllamaListParser.ParseResponse("""{"items":[{"item":"milk"},{"item":" "},{"item":"eggs","count":500}]}""");
        Assert.Equal(["milk", "eggs"], items.Select(i => i.Item));
        Assert.Equal(1, items[0].Count);
        Assert.Equal(99, items[1].Count);
    }

    [Fact]
    public void ParseResponse_ThrowsOnInvalidJson() =>
        Assert.ThrowsAny<Exception>(() => OllamaListParser.ParseResponse("not json"));

    private sealed class Stub(Func<Task<IReadOnlyList<ItemRequest>>> run) : IListParser
    {
        public Task<IReadOnlyList<ItemRequest>> ParseAsync(string text, CancellationToken ct = default) => run();
    }

    [Fact]
    public async Task Fallback_UsesPrimaryWhenItWorks()
    {
        var primary = new Stub(() => Task.FromResult<IReadOnlyList<ItemRequest>>([new ItemRequest("from llm", null, null)]));
        var parser = new FallbackListParser(primary, new RuleBasedListParser());
        Assert.Equal("from llm", Assert.Single(await parser.ParseAsync("rice")).Item);
    }

    [Fact]
    public async Task Fallback_UsesRulesWhenPrimaryThrows()
    {
        var primary = new Stub(() => throw new HttpRequestException("ollama down"));
        var parser = new FallbackListParser(primary, new RuleBasedListParser());
        Assert.Equal("rice", Assert.Single(await parser.ParseAsync("rice 1kg")).Item);
    }

    [Fact]
    public async Task Fallback_UsesRulesWhenPrimaryReturnsNothing()
    {
        var primary = new Stub(() => Task.FromResult<IReadOnlyList<ItemRequest>>([]));
        var parser = new FallbackListParser(primary, new RuleBasedListParser());
        Assert.Equal("rice", Assert.Single(await parser.ParseAsync("rice")).Item);
    }

    [Fact]
    public async Task Fallback_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var primary = new Stub(() => throw new OperationCanceledException(cts.Token));
        var parser = new FallbackListParser(primary, new RuleBasedListParser());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ParseAsync("rice", cts.Token));
    }

    [Fact]
    public async Task IsAvailable_IsFalseWhenNothingListens()
    {
        var parser = new OllamaListParser(new OllamaOptions { Url = "http://127.0.0.1:1" });
        Assert.False(await parser.IsAvailableAsync());
    }
}
