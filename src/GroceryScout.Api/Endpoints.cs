using System.Security.Cryptography;
using System.Text;
using GroceryScout.Core;
using GroceryScout.Data;
using GroceryScout.Llm;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace GroceryScout.Api;

public static class Endpoints
{
    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.AddEndpointFilter(RequireApiKey);

        api.MapGet("/health", HealthAsync);

        api.MapPost("/lists/parse", async (ParseListRequest req, IListParser parser, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Text is required." });
            if (req.Text.Length > 20_000) return Results.BadRequest(new { error = "List is too long." });
            var items = await parser.ParseAsync(req.Text, ct);
            return Results.Ok(new { items = items.Select(ItemDto.From) });
        });

        var locations = api.MapGroup("/locations");
        locations.MapGet("/", async (Store store) => await store.GetLocationsAsync());
        locations.MapPost("/", async (AddLocationRequest req, Store store) =>
        {
            if (string.IsNullOrWhiteSpace(req.Label)) return Results.BadRequest(new { error = "Label is required." });
            if (!Validation.IsPincode(req.Pincode)) return Results.BadRequest(new { error = "Pincode must be 6 digits." });
            var created = await store.AddLocationAsync(req.Label.Trim(), req.Pincode!, req.MakeDefault);
            return Results.Created($"/api/locations/{created.Id}", created);
        });
        locations.MapPut("/{id:long}/default", async (long id, Store store) =>
            await store.SetDefaultLocationAsync(id) ? Results.NoContent() : Results.NotFound());
        locations.MapDelete("/{id:long}", async (long id, Store store) =>
            await store.DeleteLocationAsync(id) ? Results.NoContent() : Results.NotFound());

        var providers = api.MapGroup("/providers");
        providers.MapGet("/", async (Store store) => await store.GetProvidersAsync());
        providers.MapPut("/{name}/enabled", async (string name, SetEnabledRequest req, Store store) =>
            await store.SetProviderEnabledAsync(name, req.Enabled) ? Results.NoContent() : Results.NotFound());
        providers.MapPut("/{name}/selectors", async (string name, SetSelectorsRequest req, Store store) =>
        {
            if (!string.IsNullOrWhiteSpace(req.SelectorConfigJson))
            {
                try { System.Text.Json.Nodes.JsonNode.Parse(req.SelectorConfigJson); }
                catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Selector overrides must be valid JSON." }); }
            }
            return await store.SetProviderSelectorsAsync(name, req.SelectorConfigJson) ? Results.NoContent() : Results.NotFound();
        });

        var runs = api.MapGroup("/runs");
        runs.MapPost("/", CreateRunAsync);
        runs.MapGet("/", async (Store store, int? limit) => await store.GetRunsAsync(Math.Clamp(limit ?? 50, 1, 200)));
        runs.MapGet("/{id:long}", async (long id, Store store) =>
            await store.GetRunDetailAsync(id) is { } d ? Results.Ok(d) : Results.NotFound());
        runs.MapPost("/{id:long}/items/{index:int}/add-to-cart", AddItemToCartAsync);
        runs.MapPost("/{id:long}/cancel", async (long id, RunWorker worker) =>
            await worker.CancelAsync(id) switch
            {
                CancelResult.NotFound => Results.NotFound(),
                CancelResult.NotCancellable => Results.Conflict(new { error = "The run has already finished." }),
                _ => Results.Accepted(),
            });
        runs.MapDelete("/{id:long}", async (long id, Store store, RunWorker worker) =>
        {
            var run = await store.GetRunAsync(id);
            if (run is null) return Results.NotFound();
            if (run.Status is RunStatus.Queued or RunStatus.Running)
                return Results.Conflict(new { error = "Cancel the run before deleting it." });
            await store.DeleteRunAsync(id);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// Adds one option of a finished item to its store's cart: the app's pick by default (e.g. a flagged smaller pack
    /// after the user approves it), or any option from the comparison list.
    /// </summary>
    private static async Task<IResult> AddItemToCartAsync(
        long id, int index, AddToCartRequest? req, Store store, RunWorker worker)
    {
        var detail = await store.GetRunDetailAsync(id);
        var item = detail?.Items.FirstOrDefault(i => i.Index == index);
        if (item is null) return Results.NotFound();
        if (item.Outcome is not { } outcome)
            return Results.Conflict(new { error = "This item has not been checked yet." });

        ProductListing? listing = outcome.Chosen;
        if (req?.ProductId is not null)
        {
            listing = outcome.Candidates.Concat(outcome.Chosen is null ? [] : [outcome.Chosen])
                .FirstOrDefault(l => l.ProductId == req.ProductId &&
                                     (req.Provider is null || l.Provider.Equals(req.Provider, StringComparison.OrdinalIgnoreCase)));
        }
        if (listing is null)
            return Results.BadRequest(new { error = "That product is not one of this item's options." });

        if (outcome.Cart is CartStatus.Added or CartStatus.Adding)
            return Results.Conflict(new { error = outcome.Cart == CartStatus.Added ? "This item was already added." : "This item is already being added." });

        await store.SetItemCartAsync(id, index, CartStatus.Adding, null, listing);
        worker.EnqueueAddToCart(id, index, listing, item.Count);
        return Results.Accepted($"/api/runs/{id}");
    }

    private static async Task<IResult> CreateRunAsync(
        CreateRunRequest req, Store store, IListParser parser, RunWorker worker, CancellationToken ct)
    {
        IReadOnlyList<ItemRequest> items;
        string rawText;
        if (req.Items is { Count: > 0 })
        {
            items = req.Items.Select(i => i.ToRequest()).ToList();
            rawText = req.Text ?? string.Join('\n', items.Select(i => i.SearchQuery()));
        }
        else if (!string.IsNullOrWhiteSpace(req.Text))
        {
            if (req.Text.Length > 20_000) return Results.BadRequest(new { error = "List is too long." });
            items = await parser.ParseAsync(req.Text, ct);
            rawText = req.Text;

            // Free-text parsing (especially by an LLM) can misread a quantity. When carts are involved,
            // a large count must be confirmed by submitting the reviewed items explicitly.
            var suspicious = items.FirstOrDefault(i => i.Count > Validation.MaxUnreviewedCount);
            if (req.AddToCart && suspicious is not null)
                return Results.BadRequest(new
                {
                    error = $"'{suspicious.Item}' was read as {suspicious.Count} packs. Review the list with POST /api/lists/parse and submit the items explicitly to add more than {Validation.MaxUnreviewedCount} of one item.",
                });
        }
        else
        {
            return Results.BadRequest(new { error = "Provide either text or items." });
        }

        if (Validation.CheckItems(items) is { } itemError) return Results.BadRequest(new { error = itemError });

        var pincode = req.Pincode;
        if (string.IsNullOrWhiteSpace(pincode)) pincode = (await store.GetDefaultLocationAsync())?.Pincode;
        if (!Validation.IsPincode(pincode))
            return Results.BadRequest(new { error = "A 6-digit pincode is required (pass one or add a default location)." });

        var id = await store.CreateRunAsync(rawText, pincode!, req.AddToCart, items, req.PreferPurchased);
        worker.Enqueue(id);
        return Results.Accepted($"/api/runs/{id}", new { id });
    }

    private static async Task<IResult> HealthAsync(
        Store store, IOptions<AppOptions> options, IHttpClientFactory http, IServiceProvider sp, CancellationToken ct)
    {
        var opts = options.Value;
        var browser = await ProbeAsync(http, opts.BrowserCdpUrl.TrimEnd('/') + "/json/version", ct);
        var ollama = opts.UseLlm && sp.GetService<OllamaListParser>() is { } p && await p.IsAvailableAsync(ct);
        var location = await store.GetDefaultLocationAsync();
        return Results.Ok(new
        {
            browser,
            ollama,
            llmEnabled = opts.UseLlm,
            llmModel = opts.UseLlm ? opts.Ollama.Model : null,
            hasDefaultLocation = location is not null,
        });
    }

    private static async Task<bool> ProbeAsync(IHttpClientFactory factory, string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await factory.CreateClient().GetAsync(url, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static async ValueTask<object?> RequireApiKey(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var expected = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value.ApiKey;
        if (string.IsNullOrEmpty(expected)) return await next(ctx);

        var provided = ctx.HttpContext.Request.Headers["X-Api-Key"].ToString();
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
        return ok ? await next(ctx) : Results.Json(new { error = "Missing or invalid X-Api-Key." }, statusCode: StatusCodes.Status401Unauthorized);
    }
}
