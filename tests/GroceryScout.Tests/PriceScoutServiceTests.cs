using GroceryScout.Core;

namespace GroceryScout.Tests;

public class PriceScoutServiceTests
{
    private sealed class FakeAdapter(string name, Func<string, IReadOnlyList<ProductListing>> search, bool locationOk = true) : IProviderAdapter
    {
        public string Name => name;
        public List<(ProductListing Listing, int Qty)> Added { get; } = [];
        public List<string> Queries { get; } = [];

        public Task SetLocationAsync(string pincode, CancellationToken ct = default) =>
            locationOk ? Task.CompletedTask : throw new InvalidOperationException("no location");

        public Task<IReadOnlyList<ProductListing>> SearchAsync(string query, CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(search(query));
        }

        public Task<CartResult> AddToCartAsync(ProductListing listing, int quantity, CancellationToken ct = default)
        {
            Added.Add((listing, quantity));
            return Task.FromResult(new CartResult(true));
        }
    }

    private sealed class Recorder : IRunObserver
    {
        public List<string> Unavailable { get; } = [];
        public List<ItemOutcome> Outcomes { get; } = [];
        public Task OnProviderUnavailableAsync(string provider, string message) { Unavailable.Add($"{provider}: {message}"); return Task.CompletedTask; }
        public Task OnItemCompletedAsync(ItemOutcome outcome) { Outcomes.Add(outcome); return Task.CompletedTask; }
    }

    private static ProductListing L(string provider, string title, decimal size, decimal price, string? brand = null) =>
        new(provider, title, brand, size, SizeUnit.Gram, price, null, true, $"https://x/{provider}/{title}", $"{provider}-{title}-{size}");

    private static readonly ItemRequest Turmeric = new("turmeric powder", 200, SizeUnit.Gram);

    [Fact]
    public async Task PicksCheapestAcrossProviders_AndAddsToThatProvidersCart()
    {
        var a = new FakeAdapter("A", _ => [L("A", "Turmeric Powder", 200, 90)]);
        var b = new FakeAdapter("B", _ => [L("B", "Turmeric Powder", 200, 70)]);
        var obs = new Recorder();

        var outcomes = await new PriceScoutService([a, b]).RunAsync([Turmeric], "560100", new RunOptions(true), obs);

        var o = Assert.Single(outcomes);
        Assert.Equal("B", o.Resolution.Chosen!.Provider);
        Assert.Equal(CartStatus.Added, o.Cart);
        Assert.Empty(a.Added);
        Assert.Equal(1, Assert.Single(b.Added).Qty);
        Assert.Single(obs.Outcomes);
        Assert.Equal("turmeric powder 200g", a.Queries.Single());
    }

    [Fact]
    public async Task DoesNotAddToCart_WhenDisabled()
    {
        var a = new FakeAdapter("A", _ => [L("A", "Turmeric Powder", 200, 90)]);
        var o = Assert.Single(await new PriceScoutService([a]).RunAsync([Turmeric], "560100", new RunOptions(false)));
        Assert.Equal(CartStatus.NotAttempted, o.Cart);
        Assert.Empty(a.Added);
    }

    [Fact]
    public async Task IrrelevantAndMultiPackListingsAreIgnored()
    {
        var a = new FakeAdapter("A", _ =>
        [
            L("A", "Red Chilli Powder", 200, 10),
            L("A", "Turmeric Powder Pack of 2", 200, 20),
            L("A", "Turmeric Powder", 200, 80),
        ]);
        var o = Assert.Single(await new PriceScoutService([a]).RunAsync([Turmeric], "560100", new RunOptions(false)));
        Assert.Equal(80m, o.Resolution.Chosen!.Price);
    }

    [Fact]
    public async Task FailingProvider_DoesNotFailTheRun_AndIsReported()
    {
        var bad = new FakeAdapter("Bad", _ => throw new TimeoutException("boom"));
        var good = new FakeAdapter("Good", _ => [L("Good", "Turmeric Powder", 200, 80)]);
        var o = Assert.Single(await new PriceScoutService([bad, good]).RunAsync([Turmeric], "560100", new RunOptions(false)));

        Assert.Equal("Good", o.Resolution.Chosen!.Provider);
        var issue = Assert.Single(o.Issues);
        Assert.Equal("Bad", issue.Provider);
    }

    [Fact]
    public async Task ProviderWithoutLocation_IsExcludedAndReported()
    {
        var noLoc = new FakeAdapter("NoLoc", _ => [L("NoLoc", "Turmeric Powder", 200, 1)], locationOk: false);
        var good = new FakeAdapter("Good", _ => [L("Good", "Turmeric Powder", 200, 80)]);
        var obs = new Recorder();

        var o = Assert.Single(await new PriceScoutService([noLoc, good]).RunAsync([Turmeric], "560100", new RunOptions(false), obs));

        Assert.Equal("Good", o.Resolution.Chosen!.Provider);
        Assert.Empty(noLoc.Queries);
        Assert.Contains("NoLoc", Assert.Single(obs.Unavailable));
    }

    [Fact]
    public async Task NoAvailableProvider_Throws()
    {
        var noLoc = new FakeAdapter("NoLoc", _ => [], locationOk: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new PriceScoutService([noLoc]).RunAsync([Turmeric], "560100", new RunOptions(false)));
    }

    [Fact]
    public async Task SmallerSizeIsFlaggedAndNotAddedToCart()
    {
        var a = new FakeAdapter("A", _ => [L("A", "Turmeric Powder", 100, 40)]);
        var o = Assert.Single(await new PriceScoutService([a]).RunAsync([Turmeric], "560100", new RunOptions(true)));

        Assert.Equal(MatchKind.SmallerSize, o.Resolution.Kind);
        Assert.Equal(CartStatus.SkippedNeedsApproval, o.Cart);
        Assert.Empty(a.Added);
    }

    [Fact]
    public async Task NotFound_IsReported()
    {
        var a = new FakeAdapter("A", _ => []);
        var o = Assert.Single(await new PriceScoutService([a]).RunAsync([Turmeric], "560100", new RunOptions(true)));
        Assert.Equal(MatchKind.NotFound, o.Resolution.Kind);
        Assert.Equal(CartStatus.SkippedNotFound, o.Cart);
    }

    [Fact]
    public async Task QuantityIsPassedToCart()
    {
        var a = new FakeAdapter("A", _ => [L("A", "Turmeric Powder", 200, 80)]);
        await new PriceScoutService([a]).RunAsync([Turmeric with { Count = 3 }], "560100", new RunOptions(true));
        Assert.Equal(3, Assert.Single(a.Added).Qty);
    }

    [Fact]
    public async Task FavouriteBrandWinsWithinTolerance_ButNotBeyondIt()
    {
        var a = new FakeAdapter("A", _ =>
        [
            L("A", "Generic Turmeric Powder", 200, 100, "Generic"),
            L("A", "Tata Turmeric Powder", 200, 108, "Tata"),
        ]);
        var service = new PriceScoutService([a]);

        var within = Assert.Single(await service.RunAsync([Turmeric with { BrandPreference = "Tata" }], "560100", new RunOptions(false)));
        Assert.Equal("Tata", within.Resolution.Chosen!.Brand);

        var beyond = Assert.Single(await service.RunAsync([Turmeric with { BrandPreference = "Tata" }], "560100", new RunOptions(false, BrandTolerance: 0.05m)));
        Assert.Equal("Generic", beyond.Resolution.Chosen!.Brand);
    }

    [Fact]
    public async Task CancellationStopsTheRun()
    {
        var a = new FakeAdapter("A", _ => [L("A", "Turmeric Powder", 200, 80)]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PriceScoutService([a]).RunAsync([Turmeric], "560100", new RunOptions(false), null, cts.Token));
    }

    // ---- concurrency --------------------------------------------------------------------------

    private sealed class Probe
    {
        private int _searching, _adding;
        public int MaxSearching, MaxAdding;
        public readonly List<string> SearchOrder = [];

        public void EnterSearch(string q) { lock (SearchOrder) { SearchOrder.Add(q); MaxSearching = Math.Max(MaxSearching, ++_searching); } }
        public void ExitSearch() { lock (SearchOrder) _searching--; }
        public void EnterAdd() { lock (SearchOrder) MaxAdding = Math.Max(MaxAdding, ++_adding); }
        public void ExitAdd() { lock (SearchOrder) _adding--; }
    }

    private sealed class SlowAdapter(string name, Probe probe, TimeSpan delay) : IProviderAdapter
    {
        public string Name => name;
        public Task SetLocationAsync(string pincode, CancellationToken ct = default) => Task.CompletedTask;

        public async Task<IReadOnlyList<ProductListing>> SearchAsync(string query, CancellationToken ct = default)
        {
            probe.EnterSearch(query);
            try
            {
                await Task.Delay(delay, ct);
                return [L(name, "Turmeric Powder", 200, 80)];
            }
            finally { probe.ExitSearch(); }
        }

        public async Task<CartResult> AddToCartAsync(ProductListing listing, int quantity, CancellationToken ct = default)
        {
            probe.EnterAdd();
            try { await Task.Delay(20, ct); return new CartResult(true); }
            finally { probe.ExitAdd(); }
        }
    }

    private static IReadOnlyList<IReadOnlyList<IProviderAdapter>> Sets(int count, Probe probe, TimeSpan delay) =>
        Enumerable.Range(0, count).Select(_ => (IReadOnlyList<IProviderAdapter>)[new SlowAdapter("A", probe, delay)]).ToList();

    private static List<ItemRequest> Items(int n) =>
        Enumerable.Range(1, n).Select(i => new ItemRequest($"turmeric powder", 200, SizeUnit.Gram, i)).ToList();

    [Fact]
    public async Task Concurrency_ProcessesItemsInParallel_AndKeepsResultsInOrder()
    {
        var probe = new Probe();
        var obs = new Recorder();
        var items = Items(6);

        var outcomes = await new PriceScoutService(Sets(3, probe, TimeSpan.FromMilliseconds(80)))
            .RunAsync(items, "560100", new RunOptions(false, Concurrency: 3), obs);

        Assert.Equal(3, probe.MaxSearching);
        Assert.Equal(Enumerable.Range(0, 6), outcomes.Select(o => o.Index));
        Assert.Equal(Enumerable.Range(1, 6), outcomes.Select(o => o.Request.Count)); // order of the list, not of completion
        Assert.Equal(6, obs.Outcomes.Count);
        Assert.Equal(Enumerable.Range(0, 6), obs.Outcomes.Select(o => o.Index).Order());
    }

    [Fact]
    public async Task Concurrency_OfOne_IsSequential()
    {
        var probe = new Probe();
        await new PriceScoutService(Sets(3, probe, TimeSpan.FromMilliseconds(30)))
            .RunAsync(Items(4), "560100", new RunOptions(false, Concurrency: 1));
        Assert.Equal(1, probe.MaxSearching);
    }

    [Fact]
    public async Task Concurrency_IsLimitedByAvailableAdapterSetsAndItemCount()
    {
        var probe = new Probe();
        await new PriceScoutService(Sets(2, probe, TimeSpan.FromMilliseconds(60)))
            .RunAsync(Items(8), "560100", new RunOptions(false, Concurrency: 10));
        Assert.Equal(2, probe.MaxSearching);

        var probe2 = new Probe();
        await new PriceScoutService(Sets(4, probe2, TimeSpan.FromMilliseconds(60)))
            .RunAsync(Items(2), "560100", new RunOptions(false, Concurrency: 4));
        Assert.Equal(2, probe2.MaxSearching);
    }

    [Fact]
    public async Task Concurrency_SerializesCartAdditions()
    {
        var probe = new Probe();
        var outcomes = await new PriceScoutService(Sets(3, probe, TimeSpan.FromMilliseconds(20)))
            .RunAsync(Items(9), "560100", new RunOptions(true, Concurrency: 3));

        Assert.All(outcomes, o => Assert.Equal(CartStatus.Added, o.Cart));
        Assert.Equal(1, probe.MaxAdding);
    }

    [Fact]
    public async Task Concurrency_CancellationStopsAllWorkers()
    {
        var probe = new Probe();
        using var cts = new CancellationTokenSource();
        var run = new PriceScoutService(Sets(3, probe, TimeSpan.FromSeconds(30)))
            .RunAsync(Items(9), "560100", new RunOptions(false, Concurrency: 3), null, cts.Token);

        await Task.Delay(150);
        await cts.CancelAsync();

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(run, finished); // returned promptly instead of waiting out the 30 s searches
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task Concurrency_ObserverFailureStopsTheRunWithThatError()
    {
        var probe = new Probe();
        var service = new PriceScoutService(Sets(3, probe, TimeSpan.FromMilliseconds(20)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunAsync(Items(9), "560100", new RunOptions(false, Concurrency: 3), new ThrowingObserver()));
        Assert.Equal("db down", ex.Message);
    }

    private sealed class ThrowingObserver : IRunObserver
    {
        public Task OnProviderUnavailableAsync(string provider, string message) => Task.CompletedTask;
        public Task OnItemCompletedAsync(ItemOutcome outcome) => throw new InvalidOperationException("db down");
    }

    private sealed class LimitedAdapter(string name, Probe probe, int limit) : IProviderAdapter, ISearchLimit
    {
        public string Name => name;
        public int MaxConcurrentSearches => limit;
        public Task SetLocationAsync(string pincode, CancellationToken ct = default) => Task.CompletedTask;

        public async Task<IReadOnlyList<ProductListing>> SearchAsync(string query, CancellationToken ct = default)
        {
            probe.EnterSearch(query);
            try { await Task.Delay(60, ct); return [L(name, "Turmeric Powder", 200, 80)]; }
            finally { probe.ExitSearch(); }
        }

        public Task<CartResult> AddToCartAsync(ProductListing listing, int quantity, CancellationToken ct = default) =>
            Task.FromResult(new CartResult(true));
    }

    [Fact]
    public async Task SearchLimit_CapsSimultaneousSearchesPerProvider_WhileOthersRunFreely()
    {
        var strict = new Probe();
        var free = new Probe();
        IReadOnlyList<IReadOnlyList<IProviderAdapter>> sets = Enumerable.Range(0, 3)
            .Select(_ => (IReadOnlyList<IProviderAdapter>)[new LimitedAdapter("Strict", strict, 1), new SlowAdapter("Free", free, TimeSpan.FromMilliseconds(60))])
            .ToList();

        var outcomes = await new PriceScoutService(sets).RunAsync(Items(9), "560100", new RunOptions(false, Concurrency: 3));

        Assert.Equal(9, outcomes.Count);
        Assert.Equal(1, strict.MaxSearching);
        Assert.Equal(3, free.MaxSearching);
    }

    [Fact]
    public async Task SearchLimit_IsIgnoredWhenNotBelowTheWorkerCount()
    {
        var probe = new Probe();
        IReadOnlyList<IReadOnlyList<IProviderAdapter>> sets = Enumerable.Range(0, 2)
            .Select(_ => (IReadOnlyList<IProviderAdapter>)[new LimitedAdapter("A", probe, 5)])
            .ToList();

        await new PriceScoutService(sets).RunAsync(Items(6), "560100", new RunOptions(false, Concurrency: 2));
        Assert.Equal(2, probe.MaxSearching);
    }

    [Fact]
    public void BlinkitIsLimitedToOneSearchAtATime_OthersAreNot()
    {
        Assert.Equal(1, new GroceryScout.Providers.BlinkitAdapter(null!).MaxConcurrentSearches);
        Assert.True(new GroceryScout.Providers.AmazonAdapter(null!).MaxConcurrentSearches > 1);
    }
}
