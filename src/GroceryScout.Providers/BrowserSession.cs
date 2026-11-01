using Microsoft.Playwright;

namespace GroceryScout.Providers;

/// <summary>
/// Headed Chromium with a persistent profile so provider logins (one-time OTP) survive restarts.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly bool _ownsBrowser;
    private readonly List<IPage> _openedPages = [];
    public IBrowserContext Context { get; }

    private BrowserSession(IPlaywright playwright, IBrowserContext context, bool ownsBrowser)
    {
        _playwright = playwright;
        Context = context;
        _ownsBrowser = ownsBrowser;
    }

    /// <summary>
    /// Attaches to a Chrome the user started with --remote-debugging-port, reusing its logged-in session.
    /// The browser is left running when this session is disposed.
    /// </summary>
    public static async Task<BrowserSession> ConnectAsync(string cdpUrl = "http://localhost:9222")
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.ConnectOverCDPAsync(cdpUrl);
        var context = browser.Contexts.FirstOrDefault()
            ?? throw new InvalidOperationException("Connected browser has no default context.");
        return new BrowserSession(playwright, context, ownsBrowser: false);
    }

    /// <param name="channel">"chrome" or "msedge" uses the installed browser (less likely to be blocked
    /// than bundled Chromium); null uses bundled Chromium.</param>
    public static async Task<BrowserSession> StartAsync(string profileDir, bool headless = false, string? channel = "chrome")
    {
        Directory.CreateDirectory(profileDir);
        var playwright = await Playwright.CreateAsync();
        var context = await playwright.Chromium.LaunchPersistentContextAsync(profileDir, new BrowserTypeLaunchPersistentContextOptions
        {
            Channel = channel,
            Headless = headless,
            ViewportSize = new ViewportSize { Width = 1366, Height = 850 },
            Locale = "en-IN",
        });
        return new BrowserSession(playwright, context, ownsBrowser: true);
    }

    public async Task<IPage> NewPageAsync()
    {
        var page = await Context.NewPageAsync();
        _openedPages.Add(page);
        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsBrowser) await Context.CloseAsync();
        else
        {
            // Leave the user's browser running, but close the tabs we opened.
            foreach (var page in _openedPages.Where(p => !p.IsClosed)) await page.CloseAsync();
        }
        _playwright.Dispose();
    }
}
