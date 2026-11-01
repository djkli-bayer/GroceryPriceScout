using System.Text.Json;
using System.Text.Json.Serialization;
using GroceryScout.Core;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models.Chat;

namespace GroceryScout.Llm;

public sealed class OllamaOptions
{
    public string Url { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen2.5:7b-instruct";
    public int TimeoutSeconds { get; set; } = 90;
}

/// <summary>
/// Turns free-form grocery text into structured items with a local LLM. The model only extracts
/// name/size text/count/brand; sizes are converted with <see cref="SizeParser"/> so unit maths never depends on it.
/// </summary>
public sealed class OllamaListParser(OllamaOptions options, ILogger<OllamaListParser>? logger = null) : IListParser
{
    private const string SystemPrompt = """
        You extract grocery items from a shopping list written by a person in India.
        Return JSON only, in this exact shape:
        {"items":[{"item":"<generic product name, lowercase, no size>","size":"<size text such as 200g, 1.5 kg, 500 ml, or null>","count":<how many packs, default 1>,"brand":"<brand if explicitly requested, else null>"}]}
        Rules:
        - One entry per product. Keep the product name generic (e.g. "turmeric powder", "basmati rice").
        - "size" is the pack size only. Never put the size inside "item".
        - "count" is the number of packs (e.g. "x2", "2 packets", "2 x 500ml" means 2). Do not confuse it with the size.
        - A number of loose pieces (e.g. "12 eggs", "half a dozen eggs", "a dozen bananas") is the SIZE in pieces ("6 pcs", "12 pcs") with count 1, never the count.
        - Only set "brand" when the user names a brand. Otherwise null.
        - Do not invent items and do not drop items.

        Examples:
        "2 packets of Tata Sampann turmeric powder 200 g" -> {"item":"turmeric powder","size":"200 g","count":2,"brand":"Tata Sampann"}
        "half a dozen eggs" -> {"item":"eggs","size":"6 pcs","count":1,"brand":null}
        "12 bananas" -> {"item":"bananas","size":"12 pcs","count":1,"brand":null}
        "milk 500ml x3" -> {"item":"milk","size":"500 ml","count":3,"brand":null}
        "salt" -> {"item":"salt","size":null,"count":1,"brand":null}
        """;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "item": { "type": "string" },
                  "size": { "type": ["string", "null"] },
                  "count": { "type": "integer" },
                  "brand": { "type": ["string", "null"] }
                },
                "required": ["item", "size", "count", "brand"]
              }
            }
          },
          "required": ["items"]
        }
        """).RootElement.Clone();

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            return await Client().IsRunningAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ItemRequest>> ParseAsync(string text, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var request = new ChatRequest
        {
            Model = options.Model,
            Stream = false,
            Format = Schema,
            Options = new OllamaSharp.Models.RequestOptions { Temperature = 0 },
            Messages =
            [
                new Message(ChatRole.System, SystemPrompt),
                new Message(ChatRole.User, text),
            ],
        };

        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in Client().ChatAsync(request, cts.Token))
            sb.Append(chunk?.Message?.Content);

        var items = ParseResponse(sb.ToString());
        logger?.LogInformation("Ollama parsed {Count} items", items.Count);
        return items;
    }

    private OllamaApiClient Client() => new(new Uri(options.Url), options.Model);

    /// <summary>Converts the model's JSON into items; public so it can be tested without a running model.</summary>
    public static IReadOnlyList<ItemRequest> ParseResponse(string json)
    {
        var dto = JsonSerializer.Deserialize<Response>(json, JsonOptions)
                  ?? throw new JsonException("Empty LLM response");
        var result = new List<ItemRequest>();
        foreach (var i in dto.Items ?? [])
        {
            var name = (i.Item ?? "").Trim();
            if (name.Length == 0) continue;

            decimal? size = null;
            SizeUnit? unit = null;
            if (!string.IsNullOrWhiteSpace(i.Size) && SizeParser.TryParse(i.Size, out var v, out var u))
            {
                size = v;
                unit = u;
            }
            var brand = string.IsNullOrWhiteSpace(i.Brand) || i.Brand.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : i.Brand.Trim();
            result.Add(new ItemRequest(name, size, unit, Math.Clamp(i.Count ?? 1, 1, 99), brand));
        }
        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed class Response { public List<Row>? Items { get; set; } }

    private sealed class Row
    {
        public string? Item { get; set; }
        public string? Size { get; set; }
        public int? Count { get; set; }
        public string? Brand { get; set; }
    }
}

/// <summary>Uses the LLM when it works and the rule-based parser otherwise (Ollama down, bad JSON, no items).</summary>
public sealed class FallbackListParser(IListParser primary, IListParser fallback, ILogger<FallbackListParser>? logger = null) : IListParser
{
    public async Task<IReadOnlyList<ItemRequest>> ParseAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var items = await primary.ParseAsync(text, ct);
            if (items.Count > 0) return items;
            logger?.LogWarning("LLM list parser returned no items; using the rule-based parser");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger?.LogWarning(ex, "LLM list parser failed; using the rule-based parser");
        }
        return await fallback.ParseAsync(text, ct);
    }
}
