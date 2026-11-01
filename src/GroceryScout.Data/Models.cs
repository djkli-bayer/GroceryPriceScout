using GroceryScout.Core;

namespace GroceryScout.Data;

public sealed record Location(long Id, string Label, string Pincode, bool IsDefault);

public sealed record ProviderSetting(string Name, bool Enabled, string? SelectorConfigJson);

public enum RunStatus { Queued, Running, Completed, Failed, Cancelled }

public enum ItemStatus { Pending, Done }

public sealed record RunSummary(
    long Id, DateTime CreatedUtc, string Pincode, bool AddToCart, RunStatus Status, string? Error, int ItemCount, int DoneCount,
    bool PreferPurchased = true);

/// <summary>What was decided for one item; stored as JSON next to the requested item.</summary>
public sealed record StoredOutcome(
    MatchKind Match,
    ProductListing? Chosen,
    IReadOnlyList<ProductListing> Candidates,
    CartStatus Cart,
    string? CartError,
    IReadOnlyList<ProviderIssue> Issues);

public sealed record RunItemRecord(
    int Index, string Item, decimal? SizeValue, SizeUnit? Unit, int Count, string? Brand,
    ItemStatus Status, StoredOutcome? Outcome);

public sealed record RunEvent(DateTime CreatedUtc, string Message);

public sealed record RunDetail(
    RunSummary Run, string RawText, IReadOnlyList<RunItemRecord> Items, IReadOnlyList<RunEvent> Events);
