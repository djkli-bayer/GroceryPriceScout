dotnet run --project tools/SpikeRunner -- <your pincode> "turmeric powder 200g"# Grocery Price Scout - Chat History / Handoff

## Goal
Personal-use AI app: take a monthly grocery list (item + size, e.g. "turmeric powder 200g"), search Amazon (Now), Blinkit, Swiggy Instamart and BigBasket, and add the cheapest listing of each item to that provider's cart. User checks out and pays manually.

## Decisions
- Frontend: React PWA (Vite).
- Backend: .NET (SDK 10.0.403, net10.0), ASP.NET Core minimal APIs, SQLite, in-process background queue.
- LLM: open source via Ollama (OllamaSharp). Hardware: HP ZBook Power 16 G11, 64 GB RAM, NVIDIA RTX 1000 Ada (4 GB VRAM). Suggested model `qwen2.5:7b-instruct` (quantized); fall back to `qwen2.5:3b` if slow. LLM only used for query parsing and fuzzy product matching; sizes parsed with regex.
- Hosting: run at home on the user's PC (home IP, free LLM, persistent browser logins). Reach from phones via Tailscale (HTTPS via `tailscale serve`) or same Wi-Fi. Optional later: always-on mini PC.
- Automation: Playwright for .NET, persistent headed browser profile per provider, one-time manual OTP login, no CAPTCHA bypass, no checkout, no payment data.
- Providers are pluggable (`IProviderAdapter`, `Provider` table with Enabled/BaseUrl/SelectorConfigJson). No cap on provider count; "max providers" is an optional setting, off by default.
- Favourite brands: score boost / price tolerance, never a blocker.
- Pincodes: user-managed `Location` table (label, pincode, IsDefault).
- Spike providers: BigBasket and Amazon Now.

## Size resolution rule
1. Exact size across all providers, cheapest wins.
2. Else the next larger size available globally (200g -> 250g -> 500g), cheapest at that size.
3. Else the largest smaller size, flagged to the user.
4. Else Not found.

## Planned solution layout
GroceryPriceScout/ GroceryScout.slnx src/GroceryScout.Core        domain: models, SizeParser, SizeResolver, IProviderAdapter src/GroceryScout.Providers   Playwright adapters src/GroceryScout.Llm         Ollama wrapper src/GroceryScout.Api         minimal API (+ SignalR later) tests/GroceryScout.Tests     xUnit tools/SpikeRunner            console spike (not yet created) web/                         React PWA (not yet created)

## Current status (authoritative; the log below is history and partly superseded)

Solution layout: `src/GroceryScout.Core` (models, SizeParser/SizeResolver, QueryMatcher, RuleBasedListParser, PriceScoutService), `.Providers` (BrowserSession over CDP, SelectorDrivenAdapter + BigBasket/Amazon/Blinkit adapters, CardExtractor/ListingBuilder), `.Llm` (OllamaListParser + FallbackListParser), `.Data` (SQLite `Store` via Dapper), `.Api` (minimal API + `RunWorker` background queue + serves the built web app from `wwwroot`), `web/` (React + Vite + TypeScript PWA; `npm run build` outputs to `src/GroceryScout.Api/wwwroot`, git-ignored), `tools/SpikeRunner`, `tools/start-chrome.ps1`, `tools/start-app.ps1` (one-click start), `tests/GroceryScout.Tests` (128 passing, run `dotnet test tests/GroceryScout.Tests`) and 62 web tests (run `npm.cmd test` in `web/`; npm.ps1 is blocked by the execution policy).

Everything below was VERIFIED live: price runs through the API and the web UI, add-to-cart via the UI button (Blinkit cart went to 2 items / Rs124 as expected), Ollama list parsing, Purchased priority on Amazon.

### Behaviour decisions made with the user
- Size rule: exact -> next larger -> flagged smaller (never auto-added) -> not found. Brand preference nudges within 10% of the cheapest.
- PURCHASED PRIORITY (user chose "always wins"): a listing the store marks as bought before (Amazon only: `<span id="{asin}-past-purchased">`, text "Purchased Jan 2025"/"Purchased 1 time") ALWAYS wins within the chosen size, even if much more expensive; among purchased, favourite brand then cheapest. Size rule still comes first. Per-run switch `preferPurchased` (default on; UI checkbox). BigBasket/Blinkit search results show no such tag.
- "Add to cart" from a finished run: `POST /api/runs/{id}/items/{index}/add-to-cart` (body optional `{provider, productId}`) for the flagged smaller pack, a failed attempt, or any option in the comparison list; queued on the same worker; new cart status `Adding`; guards: unknown product 400, unfinished item 409, already Added/Adding 409.
- Amazon Now/Fresh is not serviceable at the user's address, so the Amazon adapter uses regular Amazon.in grocery search.

### Speed work (measured)
Search per store went from 56-77 s to ~12-25 s by: reading all cards in ONE in-page call (`CardExtractor`, plain CSS + regex patterns in `SelectorConfig`; per-card Playwright calls were the bottleneck), jump-scrolling when every card is handled, stopping at the end of the loaded cards instead of the page bottom, 4 idle rounds grace, `textContent` instead of `innerText`, no per-search debug file writes (slow here). 6 items: ~275 s at concurrency 1 -> ~200 s at concurrency 2 (`GroceryScout:ItemConcurrency`, default 2, identical complete results across trials). Concurrency 3 was less reliable. Blinkit must be searched one tab at a time (`ISearchLimit`, empty pages otherwise) and has an early stop after 6 rounds without a new listing (`StaleRoundsLimit`; weak late matches can be missed); other providers do not. Do NOT `BringToFront` at the start of each search: tabs steal focus and BigBasket lazy loading stalls (incomplete results). A search that shows no cards is retried once (with BringToFront), then reported as a provider warning. Set env `GROCERYSCOUT_TRACE=1` for per-round timings. Remaining variance is store page-load latency (Amazon 2-13 s).
`tools/start-chrome.ps1` now passes `--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows`; the old debug Chrome must be fully ended (its main process outlives the window) before the new switches apply.

### Test fixtures
`tests/GroceryScout.Tests/Fixtures/*.html` are real cards captured with `SpikeRunner dump <url> --selector=<css> --sanitize` (strips hidden-input and csrf tokens); `CardExtractionTests` runs the real extraction script in headless Chromium (skipped silently if Chromium is missing). If a store changes its markup, recapture and fix selectors.

### NEXT VERSION (user asked to defer): purchase-history list
Read purchase history from all providers (Blinkit: /account/orders then each order's detail page, parse "title | size x qty | price" and "placed on" date; BigBasket and Amazon order pages not inspected yet; Amazon history mixes everything, keep grocery-like items only), detect items bought in at least 3 of the last 6 months (user's rule), pre-select them in a checklist the user can untick, then run the normal price-check/add flow ("compare all stores, add the cheapest", user's choice). Order history is personal data: keep it local in SQLite.

Known gaps / next steps:
- Instamart adapter (deferred). Phone access via Tailscale + API key not set up. Item already in the cart: Blinkit PDP then has no "Add to cart" button, so adding fails with a timeout message (not handled gracefully). Amazon add-to-cart not tested (user's cart ~51 items; ask first). BigBasket/Blinkit quantity>1 untested.
- Test items left in the user's carts: BigBasket Catch 200g, Blinkit Hamdard 200g and Whole Farm sugar 1 kg.
- Gotchas: Measure-Command swallows pipeline output; CDP screenshots can be stale (trust saved HTML); the command guard needs literal PIDs for Stop-Process; in `Program.cs` `UseRouting()` must come AFTER `UseStaticFiles()`.

## Status history (older notes)
Done (8 tests passing):
- Solution + 5 projects, references, Playwright and OllamaSharp packages, .gitignore.
- Core: `Models.cs`, `SizeParser.cs`, `SizeResolver.cs`, `IProviderAdapter.cs`.
- Tests: `SizeResolverTests.cs`.

Also done (builds clean, Chromium installed via `powershell -ExecutionPolicy Bypass -File tools/SpikeRunner/bin/Debug/net10.0/playwright.ps1 install chromium`):
- `BrowserSession.cs` (persistent headed context), `SelectorConfig.cs`, `SelectorDrivenAdapter.cs` (generic selector-driven adapter), `BigBasketAdapter.cs` (default selectors are GUESSES; override via `bigbasket.selectors.json` next to the SpikeRunner exe).
- `tools/SpikeRunner`: `dotnet run --project tools/SpikeRunner -- 560001 "turmeric powder 200g" [--add]`. Not yet run against the live site.

Not done:
- ADD-TO-CART status: BigBasket VERIFIED (basket shows Catch 200g x1; PDP header "1 Item"). Blinkit VERIFIED after a bug fix (first attempt used `div:text-is('ADD')`, which matched a Similar-products card and added the WRONG item; the main PDP button is `text='Add to cart'`; the wrong item was removed via the stepper's `button:has(.icon-minus)`). Lesson: scope product-page add selectors to the main product, not "first match on the page", and verify the cart afterwards (Blinkit header text after "Account": "1 item | ₹53"). Test items currently left in the user's carts: BigBasket Catch 200g, Blinkit Hamdard 200g. `SpikeRunner dump <url> --click=<css>` clicks an element before dumping.
- BLINKIT: (old note)  `BlinkitAdapter` search VERIFIED (cards are `div[role='button'][id]`, id = product id; Tailwind `tw-` classes; 39 relevant of 203 parsed; cheapest 200g Hamdard Rs 53). Location is session-based (set in the browser; adapter throws if the "Please provide your delivery location" popup shows). Add-to-cart via product page `https://blinkit.com/prn/product/prid/{id}` + `div:text-is('ADD')` is UNVERIFIED. To keep a tab open for the user to interact with, open it via `PUT http://127.0.0.1:9222/json/new?<url>` (Playwright-opened tabs are closed on dispose). CDP screenshots can be stale/one page behind: trust the saved HTML.
- AMAZON: Amazon Now/Fresh is not serviceable at the user's address (Bengaluru 560100) and `amazon.in/now` is just an app-download page, so `AmazonAdapter` uses regular Amazon.in grocery search (`/s?k=..&i=grocery`, cards keyed by `data-asin`, product URL `/dp/<asin>`). Search VERIFIED (20 relevant of 23, Catch 200g Rs 60); add-to-cart (`#add-to-cart-button` on product page, `#quantity` select) NOT tested. The user's real Amazon cart holds ~51 items, so never test adds without asking. `SetLocationAsync` only checks the header pincode (`#glow-ingress-line2`). Run: `--provider=amazon`. `SpikeRunner dump <url> [--selector=css]` saves page screenshot/HTML to the debug folder for selector work.
- `QueryMatcher` also drops multi-packs/combos ("Pack of 2", "3+1", "2 x 200g") unless the query asks for them.
- `QueryMatcher` (Core) filters search results to listings containing every non-size query word; needed because BigBasket pages append unrelated "recommended" products (69 parsed vs 23 relevant). The LLM matcher should later handle synonyms (e.g. haldi/turmeric). The BigBasket grid virtualizes cards, so `CollectWhileScrollingAsync` parses cards while scrolling (~75 s per search currently; can be optimised).
- BigBasket search is VERIFIED against the live site (selectors in `BigBasketAdapter.Defaults`: names, brand, size, price, MRP, stock, scrolling to lazy-load; 18 of 33 results parsed). Run with `--skip-location` when the location is already set in the debug Chrome. Add-to-cart (`--add`) reported success on BigBasket but needs the user's visual confirmation of the cart; `%LOCALAPPDATA%\GroceryScout\debug\BigBasket-after-add.png` is saved after each add. SetLocation is still unverified. Debug dumps go to `%LOCALAPPDATA%\GroceryScout\debug`.
- BigBasket (Akamai) blocked automated browsers ("Access Denied"). Workaround: attach to the user's own logged-in Chrome via CDP: run `tools/start-chrome.ps1`, log in there, then `dotnet run --project tools/SpikeRunner -- <pincode> "<item>" --cdp=http://localhost:9222`. Do not add stealth/anti-detection hacks; if still blocked, fall back to other providers or a semi-manual flow.
- Verify/fix BigBasket selectors by running the spike; then Amazon Now, Blinkit and Instamart adapters (subclass `SelectorDrivenAdapter`).
- Ollama wrapper, query parser, product matcher, API endpoints, SQLite data layer, React PWA.
- Install Chromium after build: `pwsh src/GroceryScout.Providers/bin/Debug/net10.0/playwright.ps1 install chromium`.

## Notes
- `git` is not on the PATH of the VS terminal; commit via the VS Git window.
- Next step: create the BrowserSession, BigBasketAdapter and SpikeRunner files, build, install Chromium, run the spike with the user's pincode.