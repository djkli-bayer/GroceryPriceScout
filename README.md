# GroceryPriceScout

Personal-use app: paste a monthly grocery list (item + size, e.g. `turmeric powder 200g`), and it searches
BigBasket, Amazon.in and Blinkit, picks the cheapest listing of each item and (optionally) adds it to that
provider's cart. You check out and pay yourself. No payment data, no checkout, no CAPTCHA bypass.

## How it works

- A local **Chrome** (started by `tools/start-chrome.ps1`) stays logged in to the providers. The app attaches to
  it over the Chrome DevTools port, so it uses your real sessions and delivery locations.
- For each item every provider is searched in parallel, unrelated and multi-pack listings are dropped, and the size
  rule is applied:
  1. exact size across all providers, cheapest wins;
  2. else the next larger size available, cheapest at that size;
  3. else the largest smaller size - flagged for your approval and **never** added automatically;
  4. else not found.
- A favourite brand (`brand: Tata`) wins if it costs at most 10% more than the cheapest.
- **Purchased products win.** If a store marks a product as bought before (Amazon shows "Purchased Jan 2025"), that
  product is chosen within the right size even when another store is cheaper (switch it off per run with the
  "Prefer products I've bought before" checkbox). BigBasket and Blinkit do not show such a tag.
- Everything is stored in SQLite (`%LOCALAPPDATA%\GroceryScout\scout.db`).
- Any result can be added to its store's cart afterwards with the **Add** buttons on the run page: the app's pick
  (including a smaller pack it flagged but did not add), or any option from "Compare options".

## Prerequisites

- .NET SDK 10, Google Chrome, Node.js LTS (only to build the web app).
- Optional: [Ollama](https://ollama.com) with `ollama pull qwen2.5:7b-instruct` for smarter list parsing.
  Without it the built-in rule-based parser is used.

## Run

One command (starts the debug Chrome if needed, builds the web app, starts the API):

```powershell
powershell -ExecutionPolicy Bypass -File tools\start-app.ps1
```

Then open <http://localhost:5165>, log in to BigBasket / Amazon / Blinkit in the debug Chrome window that opened,
set your delivery location there, add your pincode under **Settings**, and paste a list under **New**.

Or step by step:

```powershell
# 1. Start the debug Chrome, then log in to the stores and set your delivery location in it
powershell -ExecutionPolicy Bypass -File tools\start-chrome.ps1

# 2. Build the web app once (output goes to src/GroceryScout.Api/wwwroot)
cd web; npm install; npm run build; cd ..

# 3. Start the API + web app (http://localhost:5165)
dotnet run --project src/GroceryScout.Api
```

Web development with hot reload: run the API as above, then `cd web; npm run dev` (proxies `/api` to port 5165).
The app is an installable PWA ("Add to Home screen"); to use it from your phone away from home, expose port 5165 with
Tailscale (`tailscale serve`) and set `GroceryScout__ApiKey` so only you can use it.

Example without the web app (dry run, nothing is added to any cart):

```powershell
Invoke-RestMethod -Method Post http://localhost:5165/api/locations -ContentType application/json -Body '{"label":"Home","pincode":"560100"}'
Invoke-RestMethod -Method Post http://localhost:5165/api/runs -ContentType application/json -Body '{"text":"turmeric powder 200g\nbasmati rice 1kg x2","addToCart":false}'
Invoke-RestMethod http://localhost:5165/api/runs/1
```

List syntax: one item per line (or `;` / `,`), sizes like `200g`, `1.5kg`, `500ml`, quantity as `x2` or `2 x 500ml`,
brand as `brand: Tata`. A bare number stays part of the name (it is *not* a quantity).

## API

All routes are under `/api`; set `GroceryScout:ApiKey` to require an `X-Api-Key` header.

| Route | Purpose |
|---|---|
| `GET health` | browser / Ollama / default-location status |
| `POST lists/parse` | `{text}` -> structured items (preview before running) |
| `GET/POST locations`, `PUT locations/{id}/default`, `DELETE locations/{id}` | pincodes |
| `GET providers`, `PUT providers/{name}/enabled`, `PUT providers/{name}/selectors` | enable/disable, partial selector overrides |
| `POST runs`, `GET runs`, `GET runs/{id}`, `POST runs/{id}/cancel`, `DELETE runs/{id}` | price runs (`preferPurchased` defaults to true) |
| `POST runs/{id}/items/{index}/add-to-cart` | add the app's pick (empty body) or `{provider, productId}` from that item's options to the cart |

## Configuration (`GroceryScout` section, or `GroceryScout__Name` environment variables)

`DatabasePath`, `BrowserCdpUrl` (default `http://localhost:9222`), `ApiKey`, `UseLlm`,
`Ollama:Url`, `Ollama:Model`, `Ollama:TimeoutSeconds`, `ItemConcurrency` (items checked at once, default 2).
Set the environment variable `GROCERYSCOUT_TRACE=1` to print per-search timings.

## Speed

A search takes roughly 12-25 s per store, and several items are checked at once (`ItemConcurrency`), so a 30-item list
takes on the order of 15-20 minutes. Page-load time of the stores themselves varies. Results are read in one
browser call per scroll step; Blinkit stops scrolling once only unrelated recommendations appear, so a very weak
match far down its page can be missed.

## Tools

- `dotnet run --project tools/SpikeRunner -- 560100 "turmeric powder 200g" --cdp=http://localhost:9222 --provider=blinkit [--add]`
  tries one provider by hand. `SpikeRunner dump <url> [--selector=css] [--click=css]` saves a page's HTML for selector work
  to `%LOCALAPPDATA%\GroceryScout\debug`.

## Tests

```powershell
dotnet test tests/GroceryScout.Tests      # API, data, core logic, and the page-reading code against saved real store HTML
cd web; npm.cmd test                       # web app (use npm.cmd: npm.ps1 is blocked by PowerShell's execution policy)
```

The page-reading tests run in headless Chromium (`playwright.ps1 install chromium` once; they are skipped if it is
missing). Fixtures are real cards captured with
`SpikeRunner dump <url> --selector=<card css> --sanitize`.

Provider selectors depend on the live sites and will need maintenance when a site changes its markup; use the
selector override endpoint or `SpikeRunner dump` to diagnose.