[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ProxyTests")]

// Load .env if present. A variable already set in the real environment wins — see DotEnvLoader.
DotEnvLoader.LoadResult dotEnv = DotEnvLoader.LoadFromDefaultLocations();
if (dotEnv.Path is not null)
{
    Console.WriteLine($"  Configuration loaded from: {dotEnv.Path}");
    if (dotEnv.SkippedBecauseAlreadySet.Count > 0)
    {
        Console.WriteLine($"  Ignored in .env (already set in the environment, which wins): {string.Join(", ", dotEnv.SkippedBecauseAlreadySet)}");
    }
}

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

int port = int.TryParse(Environment.GetEnvironmentVariable("PROXY_PORT"), out int p) ? p : 11434;
string? proxyApiKey = Environment.GetEnvironmentVariable("PROXY_API_KEY");

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton<ProviderHttpClientFactory>();
builder.Services.AddSingleton<ProviderHealthService>();
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<ModelSelectionStore>();
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<ReasoningCacheService>();
builder.Services.AddSingleton<RequestTransformer>();
builder.Services.AddSingleton<OllamaResponseBuilder>();
builder.Services.AddSingleton<UsageTracker>();
builder.Services.AddSingleton<ProxyLogger>();
builder.Services.AddSingleton<ChatStreamingService>();
builder.Services.AddSingleton<UsageRollupStore>();
builder.Services.AddSingleton<FreeTierCatalogStore>();
builder.Services.AddSingleton<UsageTrackerService>();
builder.Services.AddSingleton<ProviderBillingService>();

builder.Services.AddHostedService<ProviderBenchmarkService>();
builder.Services.AddHostedService<UsageSnapshotService>();
// The roster sync is both a hosted loop and a DI-visible service (the /api/roster endpoints
// and the catalog observer need the same instance the loop runs on).
builder.Services.AddSingleton<ModelRosterSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelRosterSyncService>());

WebApplication app = builder.Build();
app.UseUpstreamErrorHandling();
app.UseOptionalProxyAuthentication(proxyApiKey);
// Serves wwwroot/: the dashboard markup and the vendored Chart.js. Static-file middleware ships
// in the Microsoft.AspNetCore.App shared framework, so this adds no package reference.
app.UseStaticFiles();

ModelCatalogService modelCatalog = app.Services.GetRequiredService<ModelCatalogService>();
ProviderRegistry providerRegistry = app.Services.GetRequiredService<ProviderRegistry>();
// The catalog feeds every discovery list to the roster sync; the sync proposes (and in
// ROSTER_MODE=sync applies) the config changes that keep each provider's offer current.
modelCatalog.RosterObserver = app.Services.GetRequiredService<ModelRosterSyncService>();
await modelCatalog.RefreshAvailableModels(CancellationToken.None);

app.MapOpenAiEndpoints();
app.MapUsageEndpoints();
app.MapDashboardEndpoints();
app.MapFreeTierEndpoints();
app.MapRosterEndpoints();
app.MapOllamaEndpoints();
app.MapHealthEndpoints();

// Plain ASCII: the Windows console defaults to codepage 850/437, which mangles
// box-drawing characters and emoji into noise.
string[] providerNames = [.. providerRegistry.Providers.Select(pv => pv.Name)];
Console.WriteLine();
Console.WriteLine($"  {ProxyVersion.Name} - one Ollama/OpenAI endpoint, many providers");
Console.WriteLine("  ==============================================================");
Console.WriteLine($"  Version   : {ProxyVersion.Current}");
Console.WriteLine($"  Providers : {providerNames.Length} ({string.Join(", ", providerNames)})");
Console.WriteLine($"  Models    : {modelCatalog.AvailableModels.Length} discovered");
Console.WriteLine($"  Default   : {providerRegistry.DefaultModel}");
Console.WriteLine($"  OpenAI API: http://localhost:{port}/v1/chat/completions");
Console.WriteLine($"  Ollama API: http://localhost:{port}/api/chat   (Visual Studio 2026 BYOM)");
Console.WriteLine($"  Dashboard : http://localhost:{port}/dashboard");
Console.WriteLine($"  Auth      : {(string.IsNullOrEmpty(proxyApiKey) ? "open (PROXY_API_KEY not set)" : "required (PROXY_API_KEY)")}");
Console.WriteLine();

app.Run();

public partial class Program { }
