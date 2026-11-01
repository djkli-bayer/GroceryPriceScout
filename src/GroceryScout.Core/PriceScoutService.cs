namespace GroceryScout.Core;

public sealed record RunOptions(
    bool AddToCart, decimal BrandTolerance = 0.10m, bool PreferPurchased = true, int Concurrency = 1);

public sealed record ProviderIssue(string Provider, string Message);

public enum CartStatus { NotAttempted, Added, Failed, SkippedNeedsApproval, SkippedNotFound, Adding }

public sealed record ItemOutcome(
    int Index,
    ItemRequest Request,
    Resolution Resolution,
    CartStatus Cart,
    string? CartError,
    IReadOnlyList<ProviderIssue> Issues);

public interface IRunObserver
{
    Task OnProviderUnavailableAsync(string provider, string message);
    Task OnItemCompletedAsync(ItemOutcome outcome);
}

/// <summary>
/// Runs a grocery list: for each item, searches every available provider in parallel, keeps relevant
/// listings, applies the size rule, and (optionally) adds the winner to that provider's cart.
/// A provider that fails for one item never fails the whole run.
/// <para>
/// With <see cref="RunOptions.Concurrency"/> above 1, several items are processed at once. Each concurrent worker
/// needs its own adapter set (own browser tabs), so the service takes one set per worker; cart additions are
/// always serialized.
/// </para>
/// </summary>
public sealed class PriceScoutService
{
    private readonly IReadOnlyList<IReadOnlyList<IProviderAdapter>> _adapterSets;

    public PriceScoutService(IReadOnlyList<IProviderAdapter> adapters) : this([adapters]) { }

    public PriceScoutService(IReadOnlyList<IReadOnlyList<IProviderAdapter>> adapterSets)
    {
        if (adapterSets.Count == 0) throw new ArgumentException("At least one adapter set is required.", nameof(adapterSets));
        _adapterSets = adapterSets;
    }

    public async Task<IReadOnlyList<ItemOutcome>> RunAsync(
        IReadOnlyList<ItemRequest> items,
        string pincode,
        RunOptions options,
        IRunObserver? observer = null,
        CancellationToken ct = default)
    {
        // The delivery location lives in the shared browser session, so checking one set is enough.
        var active = await PrepareProvidersAsync(_adapterSets[0], pincode, observer, ct);
        if (active.Count == 0)
            throw new InvalidOperationException("No provider is available. Check that the browser is running and logged in to at least one provider.");

        var activeNames = active.Select(a => a.Name).ToHashSet();
        var workerCount = Math.Clamp(options.Concurrency, 1, Math.Min(_adapterSets.Count, Math.Max(1, items.Count)));
        var slots = Enumerable.Range(0, workerCount)
            .Select(i => i == 0 ? active : _adapterSets[i].Where(a => activeNames.Contains(a.Name)).ToList())
            .ToList();

        var outcomes = new ItemOutcome?[items.Count];
        var nextIndex = -1;
        using var observerGate = new SemaphoreSlim(1, 1);
        using var cartGate = new SemaphoreSlim(1, 1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Per-provider caps on simultaneous searches (only relevant when several item workers run at once).
        var searchGates = new Dictionary<string, SemaphoreSlim>();
        foreach (var adapter in active)
            if (adapter is ISearchLimit { MaxConcurrentSearches: var max } && max < workerCount)
                searchGates[adapter.Name] = new SemaphoreSlim(Math.Max(1, max));

        try
        {
            async Task WorkerAsync(IReadOnlyList<IProviderAdapter> slot)
            {
                try
                {
                    while (true)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var i = Interlocked.Increment(ref nextIndex);
                        if (i >= items.Count) return;

                        var outcome = await ProcessItemAsync(i, items[i], slot, options, cartGate, searchGates, cts.Token);
                        outcomes[i] = outcome;

                        if (observer is null) continue;
                        await observerGate.WaitAsync(cts.Token);
                        try { await observer.OnItemCompletedAsync(outcome); }
                        finally { observerGate.Release(); }
                    }
                }
                catch
                {
                    await cts.CancelAsync(); // one worker failing or being cancelled stops the others
                    throw;
                }
            }

            await Task.WhenAll(slots.Select(s => Task.Run(() => WorkerAsync(s), CancellationToken.None)));
        }
        finally
        {
            foreach (var gate in searchGates.Values) gate.Dispose();
        }
        return outcomes.Select(o => o!).ToList();
    }

    private static async Task<List<IProviderAdapter>> PrepareProvidersAsync(
        IReadOnlyList<IProviderAdapter> adapters, string pincode, IRunObserver? observer, CancellationToken ct)
    {
        var results = await Task.WhenAll(adapters.Select(async a =>
        {
            try
            {
                await a.SetLocationAsync(pincode, ct);
                return (Adapter: a, Error: (string?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (Adapter: a, Error: ex.Message);
            }
        }));

        foreach (var failed in results.Where(r => r.Error is not null))
            if (observer is not null) await observer.OnProviderUnavailableAsync(failed.Adapter.Name, failed.Error!);

        return results.Where(r => r.Error is null).Select(r => r.Adapter).ToList();
    }

    private static async Task<ItemOutcome> ProcessItemAsync(
        int index, ItemRequest request, IReadOnlyList<IProviderAdapter> active, RunOptions options,
        SemaphoreSlim cartGate, IReadOnlyDictionary<string, SemaphoreSlim> searchGates, CancellationToken ct)
    {
        var query = request.SearchQuery();
        var searches = await Task.WhenAll(active.Select(async a =>
        {
            var gate = searchGates.GetValueOrDefault(a.Name);
            if (gate is not null) await gate.WaitAsync(ct);
            try
            {
                return (Listings: await a.SearchAsync(query, ct), Issue: (ProviderIssue?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (Listings: (IReadOnlyList<ProductListing>)[], Issue: new ProviderIssue(a.Name, ex.Message));
            }
            finally
            {
                gate?.Release();
            }
        }));

        var issues = searches.Where(s => s.Issue is not null).Select(s => s.Issue!).ToList();
        var relevant = QueryMatcher.FilterRelevant(request.Item, searches.SelectMany(s => s.Listings));
        var resolution = SizeResolver.Resolve(request, relevant, options.BrandTolerance, options.PreferPurchased);

        if (resolution.Chosen is null)
            return new ItemOutcome(index, request, resolution, CartStatus.SkippedNotFound, null, issues);

        // Substituting a smaller pack needs the user's approval, so it is only flagged.
        if (resolution.Kind == MatchKind.SmallerSize)
            return new ItemOutcome(index, request, resolution, CartStatus.SkippedNeedsApproval, null, issues);

        if (!options.AddToCart)
            return new ItemOutcome(index, request, resolution, CartStatus.NotAttempted, null, issues);

        var adapter = active.First(a => a.Name == resolution.Chosen.Provider);
        await cartGate.WaitAsync(ct);
        try
        {
            var cart = await adapter.AddToCartAsync(resolution.Chosen, request.Count, ct);
            return new ItemOutcome(index, request, resolution,
                cart.Success ? CartStatus.Added : CartStatus.Failed, cart.Error, issues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ItemOutcome(index, request, resolution, CartStatus.Failed, ex.Message, issues);
        }
        finally
        {
            cartGate.Release();
        }
    }
}
