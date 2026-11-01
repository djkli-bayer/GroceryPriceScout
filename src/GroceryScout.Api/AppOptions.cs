using GroceryScout.Llm;

namespace GroceryScout.Api;

public sealed class AppOptions
{
    public const string Section = "GroceryScout";

    /// <summary>SQLite file. Empty = %LOCALAPPDATA%\GroceryScout\scout.db.</summary>
    public string DatabasePath { get; set; } = "";

    /// <summary>Debug-port URL of the logged-in Chrome (see tools/start-chrome.ps1).</summary>
    public string BrowserCdpUrl { get; set; } = "http://localhost:9222";

    /// <summary>When set, every /api request must send it in the X-Api-Key header. Recommended whenever the app is reachable beyond localhost.</summary>
    public string ApiKey { get; set; } = "";

    public bool UseLlm { get; set; } = true;
    public OllamaOptions Ollama { get; set; } = new();

    /// <summary>
    /// How many items are checked at the same time (each uses its own browser tabs). 1 = one at a time; at most 5.
    /// 2 is the measured sweet spot: ~1.4x faster than 1 with identical results. Higher values gain little (Blinkit is
    /// limited to one search at a time) and risk incomplete results because background tabs load lazily.
    /// </summary>
    public int ItemConcurrency { get; set; } = 2;

    public string ResolveDatabasePath() =>
        string.IsNullOrWhiteSpace(DatabasePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GroceryScout", "scout.db")
            : DatabasePath;
}
