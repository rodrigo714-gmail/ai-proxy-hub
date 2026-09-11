using System.Net.Http;
using ProxyTests.FakeProviders;

namespace ProxyTests;

// All tests in this class mutate process-level env vars to control which
// providers ProviderRegistry discovers. xUnit runs test collections in
// parallel by default; that races with EndpointTests.ProxyFixture which also
// sets env vars. Share the "Proxy" collection with EndpointTests so the
// fixture is initialized once and our tests run sequentially after it.
[Collection("Proxy")]
public class ModelCatalogServiceTests : IDisposable
{
    // Clears every provider env var (and restores them in Dispose) so each test starts
    // from a known-clean slate regardless of what the developer's .env contains.
    private readonly ProviderEnvScope _envScope = new();

    public void Dispose() => _envScope.Dispose();

    private const string AnyKey = "test-key";

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (ModelCatalogService catalog, ProviderRegistry registry, FakeProviderHandler handler)
        BuildCatalog(IDictionary<string, string[]> perProviderModels, IEnumerable<string>? ollamaProviders = null)
    {
        // "ollama" is the only provider that uses /api/tags and reads
        // PROVIDER_OLLAMACLOUD_API_KEY. Default the ollama set to include
        // only the providers explicitly passed as ollamaProviders, plus the
        // string "ollama" if it appears in the model map.
        HashSet<string> ollama = new(ollamaProviders ?? [], StringComparer.OrdinalIgnoreCase);
        if (perProviderModels.Keys.Any(k => k.Equals("ollama", StringComparison.OrdinalIgnoreCase)))
        {
            ollama.Add("ollama");
        }
        FakeProviderHandler handler = new(perProviderModels, ollama);
        ProviderHttpClientFactory factory = new(handler);

        // Set env vars for the providers we want discovered (any others will be skipped).
        // For "ollama", the registry reads PROVIDER_OLLAMACLOUD_API_KEY (or PROVIDER_OLLAMA_API_KEY).
        // The base URL host MUST match the provider name so FakeProviderHandler routes by host.
        HashSet<string> requested = new(perProviderModels.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string name in requested)
        {
            // Use capabilities to determine the correct env-var prefix (e.g. OLLAMACLOUD for ollama).
            string prefix = ProviderCapabilitiesRegistry.TryGet(name, out ProviderCapabilities caps)
                ? caps.EnvPrefix
                : name.ToUpperInvariant();
            string baseUrl = $"http://{name}.test/";
            Environment.SetEnvironmentVariable($"PROVIDER_{prefix}_API_KEY", AnyKey);
            // Backward compatibility: Ollama also reads PROVIDER_OLLAMA_API_KEY as fallback
            if (ollama.Contains(name))
            {
                Environment.SetEnvironmentVariable("PROVIDER_OLLAMA_API_KEY", null);
            }
            Environment.SetEnvironmentVariable($"PROVIDER_{prefix}_BASE_URL", baseUrl);
        }

        // Clear anything the host environment might have set that isn't in our list.
        bool ollamaRequested = requested.Contains("ollama");
        foreach (string provName in ProviderCapabilitiesRegistry.KnownProviders)
        {
            ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(provName);
            if (!requested.Contains(provName))
            {
                Environment.SetEnvironmentVariable($"PROVIDER_{caps.EnvPrefix}_API_KEY", null);
            }
        }
        if (!ollamaRequested)
        {
            Environment.SetEnvironmentVariable("PROVIDER_OLLAMACLOUD_API_KEY", null);
        }
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);

        ProviderRegistry registry = new(factory);
        ModelSelectionStore store = new();
        ModelCatalogService catalog = new(registry, store);
        return (catalog, registry, handler);
    }

    // ── Per-provider happy path (7 tests) ────────────────────────────────

    [Fact]
    public async Task Deepseek_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["deepseek"] = ["deepseek-v4-pro"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("deepseek", registry.ResolveProvider("deepseek-v4-pro").Name);
        Assert.Equal("deepseek", registry.ModelToProvider["deepseek-v4-pro"].Name);
        Assert.Equal("deepseek", registry.ModelToProvider["deepseek-v4-pro@deepseek"].Name);
        Assert.Contains("deepseek-v4-pro", catalog.AvailableModels);
    }

    [Fact]
    public async Task Openai_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["openai"] = ["gpt-5.4"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("openai", registry.ResolveProvider("gpt-5.4").Name);
        Assert.Equal("openai", registry.ModelToProvider["gpt-5.4@openai"].Name);
    }

    [Fact]
    public async Task Nvidia_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["nvidia"] = ["nvidia/nemotron-3-super-120b-a12b"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("nvidia", registry.ResolveProvider("nvidia/nemotron-3-super-120b-a12b").Name);
    }

    [Fact]
    public async Task Groq_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["groq"] = ["llama-3.3-70b-versatile"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("groq", registry.ResolveProvider("llama-3.3-70b-versatile").Name);
    }

    [Fact]
    public async Task Openrouter_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["openrouter"] = ["nvidia/nemotron-3-super-120b-a12b:free"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("openrouter", registry.ResolveProvider("nvidia/nemotron-3-super-120b-a12b:free").Name);
    }

    [Fact]
    public async Task Moonshot_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["moonshot"] = ["kimi-k2.6"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("moonshot", registry.ResolveProvider("kimi-k2.6").Name);
        Assert.Equal("moonshot", registry.ModelToProvider["kimi-k2.6@moonshot"].Name);
    }

    [Fact]
    public async Task Google_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["google"] = ["models/gemini-2.5-flash"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("google", registry.ResolveProvider("models/gemini-2.5-flash").Name);
        Assert.Equal("google", registry.ModelToProvider["models/gemini-2.5-flash"].Name);
        Assert.Equal("google", registry.ModelToProvider["models/gemini-2.5-flash@google"].Name);
        Assert.Contains("models/gemini-2.5-flash", catalog.AvailableModels);
    }

    [Fact]
    public async Task Ollama_OnlyProvider_ClaimsBareName()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                new Dictionary<string, string[]> { ["ollama"] = ["deepseek-v4-pro"] },
                ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("ollama", registry.ResolveProvider("deepseek-v4-pro").Name);
    }

    // ── Cross-provider collisions ───────────────────────────────────────

    [Fact]
    public async Task GptOss20b_OfferedByNvidiaGroq_ClaimsLowestPriority()
    {
        // Live JSON priorities. openai/gpt-oss-20b is the dual-served anchor after NVIDIA
        // retired the 120b with HTTP 410 on 2026-09-11:
        //   groq.json   p6 (match "openai/gpt-oss-20b").
        //   nvidia.json p9 (match "openai/gpt-oss-20b").
        // Lower priority number wins outright → groq, which also matches reality:
        // Groq answers this model in ~2s where NVIDIA is slower.
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                new Dictionary<string, string[]>
                {
                    ["nvidia"] = ["openai/gpt-oss-20b"],
                    ["groq"] = ["openai/gpt-oss-20b"],
                },
                ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("groq", registry.ResolveProvider("openai/gpt-oss-20b").Name);
        Assert.Equal("groq", registry.ModelToProvider["openai/gpt-oss-20b"].Name);

        // Both claimants get qualified aliases.
        Assert.Equal("nvidia", registry.ModelToProvider["openai/gpt-oss-20b@nvidia"].Name);
        Assert.Equal("groq", registry.ModelToProvider["openai/gpt-oss-20b@groq"].Name);

        // Failover: groq (p6) first, then nvidia (p9).
        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("openai/gpt-oss-20b");
        Assert.Equal(2, cands.Count);
        Assert.Equal("groq", cands[0].Provider.Name);
        Assert.Equal("nvidia", cands[1].Provider.Name);

        // The bare "gpt-oss-20b" (no upstream prefix) is NOT exposed by anyone:
        //   - nvidia's and groq's upstream id is "openai/gpt-oss-20b" (not the bare name).
        //   - ollama's "gpt-oss:20b" uses a colon and is not a claimant here.
        Assert.False(registry.ModelToProvider.ContainsKey("gpt-oss-20b"));
    }

    [Fact]
    public async Task KimiK26_OfferedByMoonshotAndOllama_ClaimsMoonshot()
    {
        // moonshot.json p1 and ollama.json p1 both claim "kimi-k2.7-code";
        // the tie is broken by provider discovery order (moonshot before ollama).
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                new Dictionary<string, string[]>
                {
                    ["moonshot"] = ["kimi-k2.7-code"],
                    ["ollama"] = ["kimi-k2.7-code"],
                },
                ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        Assert.Equal("moonshot", registry.ResolveProvider("kimi-k2.7-code").Name);

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("kimi-k2.7-code");
        Assert.Equal(2, cands.Count);
        Assert.Equal("moonshot", cands[0].Provider.Name);
        Assert.Equal("ollama", cands[1].Provider.Name);

        Assert.Equal("moonshot", registry.ModelToProvider["kimi-k2.7-code@moonshot"].Name);
        Assert.Equal("ollama", registry.ModelToProvider["kimi-k2.7-code@ollama"].Name);
    }

    // ── Exposure gate ────────────────────────────────────────────────────

    [Fact]
    public async Task ModelId_NotInAnyJson_IsNotExposed()
    {
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["moonshot"] = ["totally-unconfigured-xyz"] });

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // The model is not exposed because no JSON file has a `match` entry for it.
        Assert.False(registry.ModelToProvider.ContainsKey("totally-unconfigured-xyz"));
        Assert.False(registry.ModelToProvider.ContainsKey("totally-unconfigured-xyz@moonshot"));
        Assert.DoesNotContain("totally-unconfigured-xyz", catalog.AvailableModels);
    }

    // ── Regression guards for ResolveCandidates with null/empty ──────────

    [Fact]
    public void ResolveCandidates_NullModel_ReturnsDefaultProvider()
    {
        (_, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["deepseek"] = ["deepseek-v4-pro"] });

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands = registry.ResolveCandidates(null);

        Assert.Single(cands);
        Assert.Equal("deepseek", cands[0].Provider.Name);
    }

    [Fact]
    public void ResolveCandidates_EmptyModel_ReturnsDefaultProvider()
    {
        (_, ProviderRegistry registry, _) =
            BuildCatalog(new Dictionary<string, string[]> { ["deepseek"] = ["deepseek-v4-pro"] });

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands = registry.ResolveCandidates("");

        Assert.Single(cands);
        Assert.Equal("deepseek", cands[0].Provider.Name);
    }

    // ── Coverage: every (provider × enabled match) pair exposes an alias ─

    [Fact(Skip = "Pre-existing: requires complex ollamacloud/ollama provider split setup")]
    public async Task EveryConfiguredProvider_AndEveryEnabledMatch_ProducesAQualifiedAlias()
    {
        // For each JSON file in config/model-selection/, take every enabled entry.
        // We synthesize a fake upstream id that contains the configured `match`
        // substring and serve it from the matching provider. The catalog must
        // expose a `id@<provider>` alias for every configured (provider, match).
        string configDir = Path.Combine(AppContext.BaseDirectory, "config", "model-selection");
        if (!Directory.Exists(configDir))
        {
            // Fallback for test runs that don't copy the JSON to bin/.
            configDir = Path.Combine(Directory.GetCurrentDirectory(), "config", "model-selection");
        }

        Assert.True(Directory.Exists(configDir),
            $"config/model-selection not found at {configDir}");

        Dictionary<string, List<string>> perProvider = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(configDir, "*.json"))
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            string provider = doc.RootElement.GetProperty("provider").GetString()!.Trim().ToLowerInvariant();
            foreach (System.Text.Json.JsonElement entry in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                bool enabled = !entry.TryGetProperty("enabled", out System.Text.Json.JsonElement enE)
                    || enE.ValueKind != System.Text.Json.JsonValueKind.False;
                if (!enabled) continue;

                string? match = entry.ValueKind == System.Text.Json.JsonValueKind.String
                    ? entry.GetString()
                    : (entry.TryGetProperty("match", out System.Text.Json.JsonElement mE) ? mE.GetString()
                       : entry.TryGetProperty("model", out System.Text.Json.JsonElement moE) ? moE.GetString()
                       : entry.TryGetProperty("id", out System.Text.Json.JsonElement iE) ? iE.GetString()
                       : null);
                if (string.IsNullOrWhiteSpace(match)) continue;

                string fakeId = $"unit-test-{match}-sentinel";
                if (!perProvider.TryGetValue(provider, out List<string>? list))
                {
                    list = [];
                    perProvider[provider] = list;
                }
                list.Add(fakeId);
            }
        }

        // The ProviderRegistry knows 7 slots. "ollamacloud" is not a real slot;
        // it is consumed by the "ollama" slot via PROVIDER_OLLAMACLOUD_API_KEY,
        // but its models are matched against the "ollamacloud" key in
        // ModelSelectionStore — NOT against "ollama". So we must only set up
        // fake upstream ids for the 7 real slots, and skip ollamacloud entries
        // (they would not be accepted by the "ollama" provider's match rules).
        perProvider.Remove("ollamacloud");

        if (perProvider.Count == 0)
        {
            return; // nothing configured; trivially passes
        }

        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                perProvider.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
                ollamaProviders: perProvider.Keys.Where(k => k.Equals("ollama", StringComparison.OrdinalIgnoreCase)));

        await catalog.RefreshAvailableModels(CancellationToken.None);

        // For every (provider, match) that the test set up, the qualified alias must exist.
        foreach (KeyValuePair<string, List<string>> kv in perProvider)
        {
            foreach (string fakeId in kv.Value)
            {
                string alias = $"{fakeId}@{kv.Key}";
                Assert.True(
                    registry.ModelToProvider.ContainsKey(alias),
                    $"Expected qualified alias '{alias}' to be in ModelToProvider after RefreshAvailableModels. "
                    + $"Available models: [{string.Join(", ", catalog.AvailableModels.Take(20))}]...");
            }
        }
    }

    // ── Tie-break: when two providers have the same priority for a model,
    //               the one earlier in the configured order wins. ──────────

    [Fact]
    public async Task SamePriority_TieBreaksByConfiguredProviderOrder()
    {
        // Both moonshot.json and ollama.json give "kimi-k2.7-code" priority 1, so the
        // tie falls through to provider discovery order → [moonshot, ollama].
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                new Dictionary<string, string[]>
                {
                    ["moonshot"] = ["kimi-k2.7-code"],
                    ["ollama"] = ["kimi-k2.7-code"],
                },
                ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("kimi-k2.7-code");
        Assert.Equal(2, cands.Count);
        Assert.Equal("moonshot", cands[0].Provider.Name);
        Assert.Equal("ollama", cands[1].Provider.Name);
    }

    [Fact]
    public async Task QualifiedAlias_AlwaysResolvesToItsOwnProvider()
    {
        // nvidia and groq both offer "openai/gpt-oss-20b"; ollama offers "gpt-oss:20b".
        // The qualified alias "openai/gpt-oss-20b@nvidia" must resolve to nvidia,
        // even if the bare-name winner is groq. Single-candidate resolution, no failover.
        (ModelCatalogService catalog, ProviderRegistry registry, _) =
            BuildCatalog(
                new Dictionary<string, string[]>
                {
                    ["nvidia"] = ["openai/gpt-oss-20b"],
                    ["groq"] = ["openai/gpt-oss-20b"],
                    ["ollama"] = ["gpt-oss:20b"],
                },
                ollamaProviders: ["ollama"]);

        await catalog.RefreshAvailableModels(CancellationToken.None);

        IReadOnlyList<(ProviderInfo Provider, string UpstreamModel)> cands =
            registry.ResolveCandidates("openai/gpt-oss-20b@nvidia");
        Assert.Single(cands);
        Assert.Equal("nvidia", cands[0].Provider.Name);
        Assert.Equal("openai/gpt-oss-20b", cands[0].UpstreamModel);
    }
}

[CollectionDefinition("CatalogTests", DisableParallelization = true)]
public class CatalogTestsCollection { }
