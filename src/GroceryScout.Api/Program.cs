using System.Text.Json.Serialization;
using GroceryScout.Api;
using GroceryScout.Core;
using GroceryScout.Data;
using GroceryScout.Llm;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
var options = builder.Configuration.GetSection(AppOptions.Section).Get<AppOptions>() ?? new AppOptions();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpClient();

builder.Services.AddSingleton(Store.ForFile(options.ResolveDatabasePath()));
builder.Services.AddSingleton<RuleBasedListParser>();
builder.Services.AddSingleton(sp => new OllamaListParser(options.Ollama, sp.GetRequiredService<ILogger<OllamaListParser>>()));
builder.Services.AddSingleton<IListParser>(sp => options.UseLlm
    ? new FallbackListParser(
        sp.GetRequiredService<OllamaListParser>(),
        sp.GetRequiredService<RuleBasedListParser>(),
        sp.GetRequiredService<ILogger<FallbackListParser>>())
    : sp.GetRequiredService<RuleBasedListParser>());

builder.Services.AddSingleton<RunWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunWorker>());

var app = builder.Build();

await app.Services.GetRequiredService<Store>().InitializeAsync();

if (app.Environment.WebRootPath is { } webRoot && Directory.Exists(webRoot))
    app.Logger.LogInformation("Serving the web app from {WebRoot}", webRoot);
else
    app.Logger.LogWarning("Web app not built: run `npm install && npm run build` in the web folder, then restart. The API still works.");

app.UseDefaultFiles();
app.UseStaticFiles();
// Explicit routing after static files: otherwise the catch-all fallback below matches first and static files are skipped.
app.UseRouting();
app.MapApi();

// Single-page app: any non-API path serves index.html so deep links (e.g. /#/runs/3) and reloads work.
app.MapFallbackToFile("{*path:regex(^(?!api/).*$)}", "index.html");

app.Run();

public partial class Program;
