using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using GroceryScout.Core;
using Microsoft.Data.Sqlite;

namespace GroceryScout.Data;

public sealed class Store(string connectionString)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] DefaultProviders = ["BigBasket", "Amazon", "Blinkit"];

    public static Store ForFile(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return new Store($"Data Source={path}");
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn;
    }

    public async Task InitializeAsync()
    {
        await using var conn = Open();
        await conn.ExecuteAsync("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Locations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Label TEXT NOT NULL,
                Pincode TEXT NOT NULL,
                IsDefault INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Providers (
                Name TEXT PRIMARY KEY,
                Enabled INTEGER NOT NULL DEFAULT 1,
                SelectorConfigJson TEXT NULL);
            CREATE TABLE IF NOT EXISTS Runs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CreatedUtc TEXT NOT NULL,
                Pincode TEXT NOT NULL,
                AddToCart INTEGER NOT NULL,
                Status TEXT NOT NULL,
                Error TEXT NULL,
                RawText TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS RunItems (
                RunId INTEGER NOT NULL REFERENCES Runs(Id) ON DELETE CASCADE,
                Idx INTEGER NOT NULL,
                Item TEXT NOT NULL,
                SizeValue TEXT NULL,
                Unit TEXT NULL,
                Count INTEGER NOT NULL,
                Brand TEXT NULL,
                Status TEXT NOT NULL,
                OutcomeJson TEXT NULL,
                PRIMARY KEY (RunId, Idx));
            CREATE TABLE IF NOT EXISTS RunEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId INTEGER NOT NULL REFERENCES Runs(Id) ON DELETE CASCADE,
                CreatedUtc TEXT NOT NULL,
                Message TEXT NOT NULL);
            """);

        foreach (var name in DefaultProviders)
            await conn.ExecuteAsync("INSERT OR IGNORE INTO Providers (Name, Enabled) VALUES (@name, 1)", new { name });

        // Databases created before this column existed.
        var runColumns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('Runs')")).ToHashSet();
        if (!runColumns.Contains("PreferPurchased"))
            await conn.ExecuteAsync("ALTER TABLE Runs ADD COLUMN PreferPurchased INTEGER NOT NULL DEFAULT 1");
    }

    // ---- Locations -------------------------------------------------------------------------

    public async Task<IReadOnlyList<Location>> GetLocationsAsync()
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<(long Id, string Label, string Pincode, long IsDefault)>(
            "SELECT Id, Label, Pincode, IsDefault FROM Locations ORDER BY IsDefault DESC, Label");
        return rows.Select(r => new Location(r.Id, r.Label, r.Pincode, r.IsDefault != 0)).ToList();
    }

    public async Task<Location?> GetDefaultLocationAsync() =>
        (await GetLocationsAsync()).FirstOrDefault(l => l.IsDefault) ?? (await GetLocationsAsync()).FirstOrDefault();

    public async Task<Location> AddLocationAsync(string label, string pincode, bool makeDefault)
    {
        await using var conn = Open();
        await using var tx = await conn.BeginTransactionAsync();
        var isFirst = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Locations", transaction: tx) == 0;
        var asDefault = makeDefault || isFirst;
        if (asDefault) await conn.ExecuteAsync("UPDATE Locations SET IsDefault = 0", transaction: tx);
        var id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO Locations (Label, Pincode, IsDefault) VALUES (@label, @pincode, @d); SELECT last_insert_rowid();",
            new { label, pincode, d = asDefault ? 1 : 0 }, tx);
        await tx.CommitAsync();
        return new Location(id, label, pincode, asDefault);
    }

    public async Task<bool> SetDefaultLocationAsync(long id)
    {
        await using var conn = Open();
        await using var tx = await conn.BeginTransactionAsync();
        var exists = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Locations WHERE Id = @id", new { id }, tx) > 0;
        if (!exists) return false;
        await conn.ExecuteAsync("UPDATE Locations SET IsDefault = (Id = @id)", new { id }, tx);
        await tx.CommitAsync();
        return true;
    }

    public async Task<bool> DeleteLocationAsync(long id)
    {
        await using var conn = Open();
        return await conn.ExecuteAsync("DELETE FROM Locations WHERE Id = @id", new { id }) > 0;
    }

    // ---- Providers -------------------------------------------------------------------------

    public async Task<IReadOnlyList<ProviderSetting>> GetProvidersAsync()
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<(string Name, long Enabled, string? SelectorConfigJson)>(
            "SELECT Name, Enabled, SelectorConfigJson FROM Providers ORDER BY Name");
        return rows.Select(r => new ProviderSetting(r.Name, r.Enabled != 0, r.SelectorConfigJson)).ToList();
    }

    public async Task<bool> SetProviderEnabledAsync(string name, bool enabled)
    {
        await using var conn = Open();
        return await conn.ExecuteAsync("UPDATE Providers SET Enabled = @e WHERE Name = @name COLLATE NOCASE",
            new { e = enabled ? 1 : 0, name }) > 0;
    }

    public async Task<bool> SetProviderSelectorsAsync(string name, string? selectorConfigJson)
    {
        await using var conn = Open();
        return await conn.ExecuteAsync("UPDATE Providers SET SelectorConfigJson = @json WHERE Name = @name COLLATE NOCASE",
            new { json = string.IsNullOrWhiteSpace(selectorConfigJson) ? null : selectorConfigJson, name }) > 0;
    }

    // ---- Runs ------------------------------------------------------------------------------

    public async Task<long> CreateRunAsync(
        string rawText, string pincode, bool addToCart, IReadOnlyList<ItemRequest> items, bool preferPurchased = true)
    {
        await using var conn = Open();
        await using var tx = await conn.BeginTransactionAsync();
        var runId = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO Runs (CreatedUtc, Pincode, AddToCart, Status, RawText, PreferPurchased)
            VALUES (@now, @pincode, @add, @status, @rawText, @prefer); SELECT last_insert_rowid();
            """,
            new
            {
                now = DateTime.UtcNow.ToString("O"), pincode, add = addToCart ? 1 : 0,
                status = RunStatus.Queued.ToString(), rawText, prefer = preferPurchased ? 1 : 0,
            }, tx);

        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            await conn.ExecuteAsync(
                """
                INSERT INTO RunItems (RunId, Idx, Item, SizeValue, Unit, Count, Brand, Status)
                VALUES (@runId, @i, @item, @size, @unit, @count, @brand, @status)
                """,
                new
                {
                    runId, i, item = it.Item,
                    size = it.SizeValue?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    unit = it.Unit?.ToString(), count = it.Count, brand = it.BrandPreference,
                    status = ItemStatus.Pending.ToString(),
                }, tx);
        }
        await tx.CommitAsync();
        return runId;
    }

    public async Task SetRunStatusAsync(long runId, RunStatus status, string? error = null)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("UPDATE Runs SET Status = @status, Error = @error WHERE Id = @runId",
            new { status = status.ToString(), error, runId });
    }

    public async Task SaveOutcomeAsync(long runId, ItemOutcome outcome)
    {
        var stored = new StoredOutcome(
            outcome.Resolution.Kind, outcome.Resolution.Chosen, outcome.Resolution.Candidates,
            outcome.Cart, outcome.CartError, outcome.Issues);
        await using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE RunItems SET Status = @status, OutcomeJson = @json WHERE RunId = @runId AND Idx = @idx",
            new { status = ItemStatus.Done.ToString(), json = JsonSerializer.Serialize(stored, Json), runId, idx = outcome.Index });
    }

    public async Task AddEventAsync(long runId, string message)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("INSERT INTO RunEvents (RunId, CreatedUtc, Message) VALUES (@runId, @now, @message)",
            new { runId, now = DateTime.UtcNow.ToString("O"), message });
    }

    public async Task<IReadOnlyList<RunSummary>> GetRunsAsync(int limit = 50)
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<RunRow>(
            """
            SELECT r.Id, r.CreatedUtc, r.Pincode, r.AddToCart, r.PreferPurchased, r.Status, r.Error,
                   (SELECT COUNT(*) FROM RunItems i WHERE i.RunId = r.Id) AS ItemCount,
                   (SELECT COUNT(*) FROM RunItems i WHERE i.RunId = r.Id AND i.Status = 'Done') AS DoneCount
            FROM Runs r ORDER BY r.Id DESC LIMIT @limit
            """, new { limit });
        return rows.Select(r => r.ToSummary()).ToList();
    }

    public async Task<RunSummary?> GetRunAsync(long runId) => (await GetRunDetailAsync(runId))?.Run;

    public async Task<RunDetail?> GetRunDetailAsync(long runId)
    {
        await using var conn = Open();
        var run = await conn.QuerySingleOrDefaultAsync<RunRow>(
            """
            SELECT r.Id, r.CreatedUtc, r.Pincode, r.AddToCart, r.PreferPurchased, r.Status, r.Error, r.RawText,
                   (SELECT COUNT(*) FROM RunItems i WHERE i.RunId = r.Id) AS ItemCount,
                   (SELECT COUNT(*) FROM RunItems i WHERE i.RunId = r.Id AND i.Status = 'Done') AS DoneCount
            FROM Runs r WHERE r.Id = @runId
            """, new { runId });
        if (run is null) return null;

        var items = (await conn.QueryAsync<ItemRow>(
            "SELECT Idx, Item, SizeValue, Unit, Count, Brand, Status, OutcomeJson FROM RunItems WHERE RunId = @runId ORDER BY Idx",
            new { runId })).Select(r => r.ToRecord()).ToList();

        var events = (await conn.QueryAsync<(string CreatedUtc, string Message)>(
            "SELECT CreatedUtc, Message FROM RunEvents WHERE RunId = @runId ORDER BY Id", new { runId }))
            .Select(e => new RunEvent(ParseUtc(e.CreatedUtc), e.Message)).ToList();

        return new RunDetail(run.ToSummary(), run.RawText ?? "", items, events);
    }

    /// <summary>
    /// Records the result of adding a specific listing to a cart after the run finished (or while it is in flight).
    /// <paramref name="chosen"/> replaces the stored pick so the UI always shows what was actually added.
    /// </summary>
    public async Task<bool> SetItemCartAsync(long runId, int index, CartStatus status, string? error, ProductListing? chosen = null)
    {
        await using var conn = Open();
        var json = await conn.ExecuteScalarAsync<string?>(
            "SELECT OutcomeJson FROM RunItems WHERE RunId = @runId AND Idx = @index", new { runId, index });
        if (json is null) return false;

        var outcome = JsonSerializer.Deserialize<StoredOutcome>(json, Json)!;
        var updated = outcome with { Cart = status, CartError = error, Chosen = chosen ?? outcome.Chosen };
        await conn.ExecuteAsync("UPDATE RunItems SET OutcomeJson = @json WHERE RunId = @runId AND Idx = @index",
            new { json = JsonSerializer.Serialize(updated, Json), runId, index });
        return true;
    }

    /// <summary>Called at startup: runs that were mid-flight when the app stopped can't be resumed.</summary>
    public async Task<IReadOnlyList<long>> RecoverRunsAsync()
    {
        await using var conn = Open();
        await conn.ExecuteAsync("UPDATE Runs SET Status = @failed, Error = 'Interrupted by an app restart' WHERE Status = @running",
            new { failed = RunStatus.Failed.ToString(), running = RunStatus.Running.ToString() });

        // Cart additions that were queued or in progress when the app stopped never completed.
        var stuck = await conn.QueryAsync<(long RunId, long Idx, string Json)>(
            "SELECT RunId, Idx, OutcomeJson FROM RunItems WHERE OutcomeJson LIKE '%\"cart\":\"Adding\"%'");
        foreach (var (runId, idx, json) in stuck)
        {
            var outcome = JsonSerializer.Deserialize<StoredOutcome>(json, Json)!;
            var fixedOutcome = outcome with { Cart = CartStatus.Failed, CartError = "Interrupted by an app restart" };
            await conn.ExecuteAsync("UPDATE RunItems SET OutcomeJson = @json WHERE RunId = @runId AND Idx = @idx",
                new { json = JsonSerializer.Serialize(fixedOutcome, Json), runId, idx });
        }

        var queued = await conn.QueryAsync<long>("SELECT Id FROM Runs WHERE Status = @queued ORDER BY Id",
            new { queued = RunStatus.Queued.ToString() });
        return queued.ToList();
    }

    public async Task<bool> DeleteRunAsync(long runId)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("PRAGMA foreign_keys = ON");
        await conn.ExecuteAsync("DELETE FROM RunItems WHERE RunId = @runId; DELETE FROM RunEvents WHERE RunId = @runId", new { runId });
        return await conn.ExecuteAsync("DELETE FROM Runs WHERE Id = @runId", new { runId }) > 0;
    }

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    private sealed class RunRow
    {
        public long Id { get; set; }
        public string CreatedUtc { get; set; } = "";
        public string Pincode { get; set; } = "";
        public long AddToCart { get; set; }
        public long PreferPurchased { get; set; } = 1;
        public string Status { get; set; } = "";
        public string? Error { get; set; }
        public string? RawText { get; set; }
        public long ItemCount { get; set; }
        public long DoneCount { get; set; }

        public RunSummary ToSummary() => new(
            Id, ParseUtc(CreatedUtc), Pincode, AddToCart != 0, Enum.Parse<RunStatus>(Status), Error, (int)ItemCount, (int)DoneCount,
            PreferPurchased != 0);
    }

    private sealed class ItemRow
    {
        public long Idx { get; set; }
        public string Item { get; set; } = "";
        public string? SizeValue { get; set; }
        public string? Unit { get; set; }
        public long Count { get; set; }
        public string? Brand { get; set; }
        public string Status { get; set; } = "";
        public string? OutcomeJson { get; set; }

        public RunItemRecord ToRecord() => new(
            (int)Idx, Item,
            SizeValue is null ? null : decimal.Parse(SizeValue, System.Globalization.CultureInfo.InvariantCulture),
            Unit is null ? null : Enum.Parse<SizeUnit>(Unit),
            (int)Count, Brand, Enum.Parse<ItemStatus>(Status),
            OutcomeJson is null ? null : JsonSerializer.Deserialize<StoredOutcome>(OutcomeJson, Json));
    }
}
