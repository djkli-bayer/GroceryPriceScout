param([int]$Port = 9222)

# Starts your real Chrome with a dedicated profile and a local-only debugging port.
# Log in to the grocery sites once in this window; the app attaches to it.
#
# The three --disable-* switches stop Chrome from slowing down background tabs and covered windows.
# The app searches several stores at once in separate tabs, and a throttled tab loads results about
# twice as slowly (and much slower when several items are checked at once).
$profile = Join-Path $env:LOCALAPPDATA "GroceryScout\chrome-profile"
$chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
Start-Process $chrome -ArgumentList @(
    "--remote-debugging-port=$Port",
    "--remote-debugging-address=127.0.0.1",
    "--user-data-dir=`"$profile`"",
    "--disable-background-timer-throttling",
    "--disable-renderer-backgrounding",
    "--disable-backgrounding-occluded-windows",
    "https://www.bigbasket.com/"
)
