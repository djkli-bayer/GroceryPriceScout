param(
    [switch]$SkipWebBuild,   # reuse the already-built web app
    [int]$Port = 9222
)

# One-click start: debug Chrome (if not already running) + API serving the web app.
$root = Split-Path -Parent $PSScriptRoot
$env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")

$chromeUp = $false
try { $chromeUp = (Invoke-WebRequest "http://127.0.0.1:$Port/json/version" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { }
if (-not $chromeUp) {
    Write-Host "Starting the debug Chrome - log in to BigBasket / Amazon / Blinkit there if needed."
    & (Join-Path $PSScriptRoot "start-chrome.ps1") -Port $Port
} else {
    Write-Host "Debug Chrome already running."
}

$wwwroot = Join-Path $root "src\GroceryScout.Api\wwwroot\index.html"
if (-not $SkipWebBuild -and (Get-Command node -ErrorAction SilentlyContinue)) {
    Push-Location (Join-Path $root "web")
    if (-not (Test-Path "node_modules")) { npm.cmd install }
    npm.cmd run build
    Pop-Location
} elseif (-not (Test-Path $wwwroot)) {
    Write-Warning "The web app is not built and Node.js was not found; only the API will be available."
}

Write-Host "Open http://localhost:5165 (Ctrl+C to stop)"
dotnet run --project (Join-Path $root "src\GroceryScout.Api") --urls http://localhost:5165
