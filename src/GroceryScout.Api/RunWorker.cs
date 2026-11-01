using System.Collections.Concurrent;
using System.Threading.Channels;
using GroceryScout.Core;
using GroceryScout.Data;
using GroceryScout.Providers;
using Microsoft.Extensions.Options;

namespace GroceryScout.Api;

public enum CancelResult { NotFound, Cancelled, NotCancellable }

/// <summary>
/// Executes queued jobs one at a time (the shared browser can only do one thing at once): price runs, whose progress
/// is recorded in the store as each item completes, and single "add this listing to the cart" requests.
/// </summary>
public sealed class RunWorker(Store store, IOptions<AppOptions> options, ILogger<RunWorker> logger) : BackgroundService
{
    private abstract record Job;
    private sealed record RunJob(long RunId) : Job;
    private sealed record AddJob(long RunId, int Index, ProductListing Listing, int Quantity) : Job;

    private readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _active = new();

    public void Enqueue(long runId) => _queue.Writer.TryWrite(new RunJob(runId));

    public void EnqueueAddToCart(long runId, int index, ProductListing listing, int quantity) =>
        _queue.Writer.TryWrite(new AddJob(runId, index, listing, quantity));

    public async Task<CancelResult> CancelAsync(long runId)
    {
        var run = await store.GetRunAsync(runId);
        if (run is null) return CancelResult.NotFound;

        if (_active.TryGetValue(runId, out var cts))
        {
            await cts.CancelAsync();
            return CancelResult.Cancelled;
        }
        if (run.Status == RunStatus.Queued)
        {
            await store.SetRunStatusAsync(runId, RunStatus.Cancelled);
            return CancelResult.Cancelled;
        }
        return CancelResult.NotCancellable;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var id in await store.RecoverRunsAsync()) Enqueue(id);

        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                switch (job)
                {
                    case RunJob run: await ExecuteRunAsync(run.RunId, stoppingToken); break;
                    case AddJob add: await ExecuteAddAsync(add, stoppingToken); break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job {Job} crashed", job);
                if (job is RunJob r) await store.SetRunStatusAsync(r.RunId, RunStatus.Failed, ex.Message);
                else if (job is AddJob a) await store.SetItemCartAsync(a.RunId, a.Index, CartStatus.Failed, ex.Message);
            }
        }
    }

    private async Task ExecuteAddAsync(AddJob job, CancellationToken ct)
    {
        BrowserSession browser;
        try
        {
            browser = await BrowserSession.ConnectAsync(options.Value.BrowserCdpUrl);
        }
        catch (Exception ex)
        {
            await store.SetItemCartAsync(job.RunId, job.Index, CartStatus.Failed,
                $"Could not connect to the browser at {options.Value.BrowserCdpUrl}. Start it with tools\\start-chrome.ps1. ({ex.Message})");
            return;
        }

        await using (browser)
        {
            var adapter = ProviderFactory.CreateFor(browser, await store.GetProvidersAsync(), job.Listing.Provider);
            if (adapter is null)
            {
                await store.SetItemCartAsync(job.RunId, job.Index, CartStatus.Failed, $"Unknown store '{job.Listing.Provider}'.");
                return;
            }

            var result = await adapter.AddToCartAsync(job.Listing, job.Quantity, ct);
            await store.SetItemCartAsync(job.RunId, job.Index,
                result.Success ? CartStatus.Added : CartStatus.Failed, result.Error, job.Listing);
        }
    }

    private async Task ExecuteRunAsync(long runId, CancellationToken stoppingToken)
    {
        var detail = await store.GetRunDetailAsync(runId);
        if (detail is null || detail.Run.Status != RunStatus.Queued) return; // cancelled or deleted while waiting

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _active[runId] = cts;
        try
        {
            await store.SetRunStatusAsync(runId, RunStatus.Running);

            BrowserSession browser;
            try
            {
                browser = await BrowserSession.ConnectAsync(options.Value.BrowserCdpUrl);
            }
            catch (Exception ex)
            {
                await store.SetRunStatusAsync(runId, RunStatus.Failed,
                    $"Could not connect to the browser at {options.Value.BrowserCdpUrl}. Start it with tools\\start-chrome.ps1 and log in to the providers. ({ex.Message})");
                return;
            }

            await using (browser)
            {
                var settings = await store.GetProvidersAsync();
                var items = detail.Items
                    .Select(i => new ItemRequest(i.Item, i.SizeValue, i.Unit, i.Count, i.Brand))
                    .ToList();

                // One adapter set (= its own browser tabs) per item worker running at the same time.
                var workers = Math.Clamp(options.Value.ItemConcurrency, 1, 5);
                workers = Math.Min(workers, Math.Max(1, items.Count));
                var adapterSets = Enumerable.Range(0, workers).Select(_ => ProviderFactory.Create(browser, settings)).ToList();
                if (adapterSets[0].Count == 0)
                {
                    await store.SetRunStatusAsync(runId, RunStatus.Failed, "No providers are enabled.");
                    return;
                }

                var observer = new StoreObserver(store, runId);

                try
                {
                    await new PriceScoutService(adapterSets).RunAsync(
                        items, detail.Run.Pincode,
                        new RunOptions(detail.Run.AddToCart, PreferPurchased: detail.Run.PreferPurchased, Concurrency: workers),
                        observer, cts.Token);
                    await store.SetRunStatusAsync(runId, RunStatus.Completed);
                }
                catch (OperationCanceledException)
                {
                    await store.SetRunStatusAsync(runId, RunStatus.Cancelled);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Run {RunId} failed", runId);
                    await store.SetRunStatusAsync(runId, RunStatus.Failed, ex.Message);
                }
            }
        }
        finally
        {
            _active.TryRemove(runId, out _);
        }
    }

    private sealed class StoreObserver(Store store, long runId) : IRunObserver
    {
        public Task OnProviderUnavailableAsync(string provider, string message) =>
            store.AddEventAsync(runId, $"{provider} unavailable: {message}");

        public Task OnItemCompletedAsync(ItemOutcome outcome) => store.SaveOutcomeAsync(runId, outcome);
    }
}
