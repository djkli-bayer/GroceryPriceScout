using GroceryScout.Core;
using GroceryScout.Data;

namespace GroceryScout.Tests;

public sealed class StoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"groceryscout-{Guid.NewGuid():N}.db");
    private Store _store = null!;

    public async Task InitializeAsync()
    {
        _store = Store.ForFile(_path);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_path) + "*"))
            File.Delete(f);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InitializeIsIdempotent_AndSeedsProviders()
    {
        await _store.InitializeAsync();
        var providers = await _store.GetProvidersAsync();
        Assert.Equal(["Amazon", "BigBasket", "Blinkit"], providers.Select(p => p.Name));
        Assert.All(providers, p => Assert.True(p.Enabled));
    }

    [Fact]
    public async Task ProviderCanBeDisabled()
    {
        Assert.True(await _store.SetProviderEnabledAsync("amazon", false));
        Assert.False((await _store.GetProvidersAsync()).Single(p => p.Name == "Amazon").Enabled);
        Assert.False(await _store.SetProviderEnabledAsync("Nope", false));
    }

    [Fact]
    public async Task FirstLocationBecomesDefault_AndDefaultCanMove()
    {
        var home = await _store.AddLocationAsync("Home", "560100", false);
        var office = await _store.AddLocationAsync("Office", "560001", false);
        Assert.True(home.IsDefault);
        Assert.False(office.IsDefault);

        Assert.True(await _store.SetDefaultLocationAsync(office.Id));
        Assert.Equal("560001", (await _store.GetDefaultLocationAsync())!.Pincode);
        Assert.Single(await _store.GetLocationsAsync(), l => l.IsDefault);

        Assert.True(await _store.DeleteLocationAsync(office.Id));
        Assert.Single(await _store.GetLocationsAsync());
        Assert.False(await _store.SetDefaultLocationAsync(999));
    }

    [Fact]
    public async Task RunLifecycle_PersistsItemsAndOutcomes()
    {
        var items = new[]
        {
            new ItemRequest("turmeric powder", 200, SizeUnit.Gram, 2, "Tata"),
            new ItemRequest("tomatoes", null, null),
        };
        var runId = await _store.CreateRunAsync("turmeric powder 200g x2\ntomatoes", "560100", true, items);

        var queued = await _store.GetRunDetailAsync(runId);
        Assert.Equal(RunStatus.Queued, queued!.Run.Status);
        Assert.Equal(2, queued.Run.ItemCount);
        Assert.Equal(0, queued.Run.DoneCount);
        Assert.Equal(200m, queued.Items[0].SizeValue);
        Assert.Equal("Tata", queued.Items[0].Brand);
        Assert.Equal(2, queued.Items[0].Count);
        Assert.Null(queued.Items[1].SizeValue);

        var listing = new ProductListing("Blinkit", "Hamdard Turmeric", "Hamdard", 200, SizeUnit.Gram, 53.5m, 100m, true, "https://b/x", "507104");
        var outcome = new ItemOutcome(0, items[0], new Resolution(MatchKind.Exact, listing, [listing]),
            CartStatus.Added, null, [new ProviderIssue("Amazon", "timeout")]);

        await _store.SetRunStatusAsync(runId, RunStatus.Running);
        await _store.SaveOutcomeAsync(runId, outcome);
        await _store.AddEventAsync(runId, "BigBasket unavailable");
        await _store.SetRunStatusAsync(runId, RunStatus.Completed);

        var done = await _store.GetRunDetailAsync(runId);
        Assert.Equal(RunStatus.Completed, done!.Run.Status);
        Assert.Equal(1, done.Run.DoneCount);
        var first = done.Items[0];
        Assert.Equal(ItemStatus.Done, first.Status);
        Assert.Equal(MatchKind.Exact, first.Outcome!.Match);
        Assert.Equal(53.5m, first.Outcome.Chosen!.Price);
        Assert.Equal(CartStatus.Added, first.Outcome.Cart);
        Assert.Equal("Amazon", Assert.Single(first.Outcome.Issues).Provider);
        Assert.Equal(ItemStatus.Pending, done.Items[1].Status);
        Assert.Equal("BigBasket unavailable", Assert.Single(done.Events).Message);

        var list = await _store.GetRunsAsync();
        Assert.Equal(runId, Assert.Single(list).Id);
    }

    [Fact]
    public async Task RecoverRuns_FailsRunningRunsAndReturnsQueuedOnes()
    {
        var item = new[] { new ItemRequest("rice", null, null) };
        var running = await _store.CreateRunAsync("rice", "560100", false, item);
        var queued = await _store.CreateRunAsync("rice", "560100", false, item);
        await _store.SetRunStatusAsync(running, RunStatus.Running);

        var toRequeue = await _store.RecoverRunsAsync();

        Assert.Equal([queued], toRequeue);
        var r = await _store.GetRunAsync(running);
        Assert.Equal(RunStatus.Failed, r!.Status);
        Assert.Contains("restart", r.Error);
    }

    [Fact]
    public async Task RecoverRuns_FailsCartAdditionsThatNeverFinished()
    {
        var item = new ItemRequest("rice", null, null);
        var runId = await _store.CreateRunAsync("rice", "560100", false, [item]);
        var listing = new ProductListing("Blinkit", "Rice", null, 1000, SizeUnit.Gram, 90, null, true, "u", "r1");
        await _store.SaveOutcomeAsync(runId, new ItemOutcome(0, item, new Resolution(MatchKind.Exact, listing, [listing]), CartStatus.Adding, null, []));

        await _store.RecoverRunsAsync();

        var outcome = (await _store.GetRunDetailAsync(runId))!.Items[0].Outcome!;
        Assert.Equal(CartStatus.Failed, outcome.Cart);
        Assert.Contains("restart", outcome.CartError);
    }

    [Fact]
    public async Task SetItemCart_UpdatesStatusAndChosenListing_ButNotForPendingItems()
    {
        var item = new ItemRequest("rice", null, null);
        var runId = await _store.CreateRunAsync("rice", "560100", false, [item, item]);
        var first = new ProductListing("Blinkit", "Rice A", null, 1000, SizeUnit.Gram, 90, null, true, "u", "a");
        var second = first with { Provider = "BigBasket", ProductId = "b", Price = 95 };
        await _store.SaveOutcomeAsync(runId, new ItemOutcome(0, item, new Resolution(MatchKind.Exact, first, [first, second]), CartStatus.NotAttempted, null, []));

        Assert.True(await _store.SetItemCartAsync(runId, 0, CartStatus.Added, null, second));
        Assert.False(await _store.SetItemCartAsync(runId, 1, CartStatus.Added, null)); // never checked

        var outcome = (await _store.GetRunDetailAsync(runId))!.Items[0].Outcome!;
        Assert.Equal(CartStatus.Added, outcome.Cart);
        Assert.Equal("b", outcome.Chosen!.ProductId);
        Assert.Equal(2, outcome.Candidates.Count);
    }

    [Fact]
    public async Task DeleteRun_RemovesItsRows()
    {
        var runId = await _store.CreateRunAsync("rice", "560100", false, [new ItemRequest("rice", null, null)]);
        await _store.AddEventAsync(runId, "x");
        Assert.True(await _store.DeleteRunAsync(runId));
        Assert.Null(await _store.GetRunDetailAsync(runId));
        Assert.False(await _store.DeleteRunAsync(runId));
    }

    [Fact]
    public async Task PreferPurchased_IsStoredPerRun_AndDefaultsToOn()
    {
        var item = new[] { new ItemRequest("rice", null, null) };
        var on = await _store.CreateRunAsync("rice", "560100", false, item);
        var off = await _store.CreateRunAsync("rice", "560100", false, item, preferPurchased: false);

        Assert.True((await _store.GetRunAsync(on))!.PreferPurchased);
        Assert.False((await _store.GetRunAsync(off))!.PreferPurchased);
        Assert.Equal([false, true], (await _store.GetRunsAsync()).Select(r => r.PreferPurchased));
    }

    [Fact]
    public async Task Purchased_RoundTripsThroughStoredOutcome()
    {
        var item = new ItemRequest("turmeric powder", 200, SizeUnit.Gram);
        var runId = await _store.CreateRunAsync("t", "560100", false, [item]);
        var listing = new ProductListing("Amazon", "Everest", "Everest", 200, SizeUnit.Gram, 94, null, true, "u", "B0X",
            Purchased: true, PurchasedNote: "Jan 2025");
        await _store.SaveOutcomeAsync(runId,
            new ItemOutcome(0, item, new Resolution(MatchKind.Exact, listing, [listing]), CartStatus.NotAttempted, null, []));

        var chosen = (await _store.GetRunDetailAsync(runId))!.Items[0].Outcome!.Chosen!;
        Assert.True(chosen.Purchased);
        Assert.Equal("Jan 2025", chosen.PurchasedNote);
    }

    [Fact]
    public async Task InitializeMigratesDatabasesCreatedBeforePreferPurchasedExisted()
    {
        var oldPath = Path.Combine(Path.GetTempPath(), $"groceryscout-old-{Guid.NewGuid():N}.db");
        try
        {
            await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldPath}"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE Runs (Id INTEGER PRIMARY KEY AUTOINCREMENT, CreatedUtc TEXT NOT NULL, Pincode TEXT NOT NULL,
                        AddToCart INTEGER NOT NULL, Status TEXT NOT NULL, Error TEXT NULL, RawText TEXT NOT NULL);
                    INSERT INTO Runs (CreatedUtc, Pincode, AddToCart, Status, RawText)
                        VALUES ('2026-10-31T20:00:00.0000000Z', '560100', 0, 'Completed', 'rice');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var store = Store.ForFile(oldPath);
            await store.InitializeAsync();
            await store.InitializeAsync(); // twice: the migration must be idempotent

            var run = Assert.Single(await store.GetRunsAsync());
            Assert.True(run.PreferPurchased);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(oldPath) + "*")) File.Delete(f);
        }
    }
}
