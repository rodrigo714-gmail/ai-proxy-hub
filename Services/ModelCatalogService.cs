using System.Text.Json;

internal sealed class ModelCatalogService
{
    private readonly ProviderRegistry _providerRegistry;
    private readonly ModelSelectionStore _modelSelectionStore;
    private readonly TimeSpan _modelsRefreshInterval = TimeSpan.FromMinutes(5);

    public ModelCatalogService(ProviderRegistry providerRegistry, ModelSelectionStore modelSelectionStore)
    {
        _providerRegistry = providerRegistry;
        _modelSelectionStore = modelSelectionStore;
        AvailableModels = [_providerRegistry.DefaultModel];
        ModelsLastRefreshUtc = DateTime.MinValue;
        // Load static aliases from config immediately — these come from JSON config files,
        // not from provider API discovery, so they're available before any API call.
        LoadStaticAliasesFromConfig();
    }

    internal string[] AvailableModels { get; private set; }

    internal DateTime ModelsLastRefreshUtc { get; private set; }

    /// <summary>
    /// Optional sink that receives every raw discovery list so the roster sync can propose
    /// additions and retirements. Wired in <c>Program.cs</c> after the container is built —
    /// the catalog itself never depends on the sync service.
    /// </summary>
    internal IModelRosterObserver? RosterObserver { get; set; }

    internal async Task RefreshAvailableModelsIfNeeded(CancellationToken ct)
    {
        if (DateTime.UtcNow - ModelsLastRefreshUtc < _modelsRefreshInterval)
            return;
        await RefreshAvailableModels(ct);
    }

    internal async Task RefreshAvailableModels(CancellationToken ct)
    {
        try
        {
            var claimsByUpstream = new Dictionary<string, List<Claimant>>(StringComparer.OrdinalIgnoreCase);

            foreach (ProviderInfo prov in _providerRegistry.Providers)
            {
                string[] discovered = await TryGetModelsFromProvider(prov, ct);
                RosterObserver?.Observe(prov.Name, discovered);
                foreach (string m in discovered)
                {
                    if (string.IsNullOrWhiteSpace(m)) continue;
                    if (!_modelSelectionStore.IsPreferredModel(m, prov.Name)) continue;
                    (int ctx, _, _, _, _, _) = GetModelProfile(m);
                    if (ctx == 0) continue;
                    int prio = _modelSelectionStore.GetPreferredModelPriority(m, prov.Name);
                    if (!claimsByUpstream.TryGetValue(m, out var list))
                    {
                        list = [];
                        claimsByUpstream[m] = list;
                    }
                    list.Add(new Claimant(prov, m, prio));
                }
            }

            var providerOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _providerRegistry.Providers.Count; i++)
                providerOrder[_providerRegistry.Providers[i].Name] = i;

            var newMap = new Dictionary<string, ProviderInfo>(StringComparer.OrdinalIgnoreCase);
            var newUpstream = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var allModels = new List<string>();

            foreach (var kv in claimsByUpstream)
            {
                var ordered = kv.Value
                    .OrderBy(c => c.Priority)
                    .ThenBy(c => providerOrder.TryGetValue(c.Provider.Name, out int o) ? o : int.MaxValue)
                    .ToList();

                foreach (var c in ordered)
                {
                    string qualified = $"{c.UpstreamId}@{c.Provider.Name}";
                    if (!newMap.ContainsKey(qualified))
                    {
                        newMap[qualified] = c.Provider;
                        newUpstream[qualified] = c.UpstreamId;
                        allModels.Add(qualified);
                    }
                }

                var winner = ordered[0];
                string bare = winner.UpstreamId;
                if (!newMap.ContainsKey(bare))
                {
                    newMap[bare] = winner.Provider;
                    newUpstream[bare] = bare;
                    allModels.Add(bare);
                }
            }

            // Always merge static aliases from config
            MergeAliases(newMap, newUpstream, allModels);
            RebuildAutoAliases(newMap, newUpstream, allModels);

            if (allModels.Count > 0)
            {
                AvailableModels = [.. allModels.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];

                var upstreamToProviders = new Dictionary<string, List<ProviderInfo>>(StringComparer.OrdinalIgnoreCase);
                foreach (string upstreamId in claimsByUpstream.Keys)
                {
                    var ordered = claimsByUpstream[upstreamId]
                        .OrderBy(c => c.Priority)
                        .ThenBy(c => providerOrder.TryGetValue(c.Provider.Name, out int o) ? o : int.MaxValue)
                        .ToList();
                    upstreamToProviders[upstreamId] = [.. ordered.Select(c => c.Provider).Distinct(ProviderInfoNameComparer.Instance)];
                }
                _providerRegistry.UpdateModelMappings(newMap, newUpstream, upstreamToProviders);
                ModelsLastRefreshUtc = DateTime.UtcNow;
            }
        }
        catch { }
    }

    /// <summary>
    /// Loads model aliases from config files (the "upstream" field in model-selection/*.json).
    /// These are available immediately at startup, before any provider API calls.
    /// </summary>
    private void LoadStaticAliasesFromConfig()
    {
        var newMap = new Dictionary<string, ProviderInfo>(_providerRegistry.ModelToProvider, StringComparer.OrdinalIgnoreCase);
        var newUpstream = new Dictionary<string, string>(_providerRegistry.ModelToUpstream, StringComparer.OrdinalIgnoreCase);
        var models = new List<string>(newMap.Keys);

        foreach (var provider in _providerRegistry.Providers)
        {
            var entries = _modelSelectionStore.GetProviderModelSelections(provider.Name);
            foreach (var entry in entries)
            {
                if (!entry.Enabled) continue;

                // Upstream defaults to match when not specified
                string up = string.IsNullOrWhiteSpace(entry.Upstream) ? entry.Match : entry.Upstream;

                // Always register a qualified alias: match@provider
                string qualified = $"{entry.Match}@{provider.Name}";
                if (!newMap.ContainsKey(qualified))
                {
                    newMap[qualified] = provider;
                    newUpstream[qualified] = up;
                    if (!models.Contains(qualified)) models.Add(qualified);
                }

                // Register upstream if different from match
                if (!string.Equals(entry.Match, up, StringComparison.OrdinalIgnoreCase))
                {
                    if (!newMap.ContainsKey(up))
                    {
                        newMap[up] = provider;
                        newUpstream[up] = up;
                        if (!models.Contains(up)) models.Add(up);
                    }
                }

                // Register bare match
                if (!newMap.ContainsKey(entry.Match))
                {
                    newMap[entry.Match] = provider;
                    newUpstream[entry.Match] = up;
                    if (!models.Contains(entry.Match)) models.Add(entry.Match);
                }
            }
        }

        RebuildAutoAliases(newMap, newUpstream, models);

        if (models.Count > 0)
        {
            _providerRegistry.UpdateModelMappings(newMap, newUpstream);
            string defaultModel = _providerRegistry.DefaultModel;
            AvailableModels = [defaultModel, .. models.Where(m => !string.Equals(m, defaultModel, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m)];
        }
    }

    /// <summary>
    /// Merges alias entries from config into discovered model data.
    /// </summary>
    private void MergeAliases(Dictionary<string, ProviderInfo> map, Dictionary<string, string> upstream, List<string> models)
    {
        foreach (var provider in _providerRegistry.Providers)
        {
            var entries = _modelSelectionStore.GetProviderModelSelections(provider.Name);
            foreach (var entry in entries)
            {
                if (!entry.Enabled) continue;

                // Upstream defaults to match when not specified
                string up = string.IsNullOrWhiteSpace(entry.Upstream) ? entry.Match : entry.Upstream;

                // Always register a qualified alias: match@provider
                string qualified = $"{entry.Match}@{provider.Name}";
                if (!map.ContainsKey(qualified))
                {
                    map[qualified] = provider;
                    upstream[qualified] = up;
                    if (!models.Contains(qualified)) models.Add(qualified);
                }

                // Register upstream if different from match
                if (!string.Equals(entry.Match, up, StringComparison.OrdinalIgnoreCase))
                {
                    if (!map.ContainsKey(up))
                    {
                        map[up] = provider;
                        upstream[up] = up;
                        if (!models.Contains(up)) models.Add(up);
                    }
                }

                // Register bare match
                if (!map.ContainsKey(entry.Match))
                {
                    map[entry.Match] = provider;
                    upstream[entry.Match] = up;
                    if (!models.Contains(entry.Match)) models.Add(entry.Match);
                }
            }
        }
    }

    /// <summary>
    /// The identity under which the same model offered by different providers becomes one choice.
    /// Providers publish it under different ids — Cerebras <c>gpt-oss-120b</c>, Groq and NVIDIA
    /// <c>openai/gpt-oss-120b</c>, Ollama <c>gpt-oss:120b</c> — so the vendor prefix is dropped and
    /// the colon of an Ollama size tag folded to a dash.
    ///
    /// Deliberately conservative. Failing to group two spellings of one model merely omits an AUTO
    /// entry; grouping two genuinely different models would send a request to a model the user
    /// never picked, which is far worse. That is why nothing here strips version or variant
    /// suffixes: <c>nemotron-3-super-120b-a12b</c> and <c>…-a12b:free</c> stay separate.
    /// </summary>
    internal static string AutoAliasKey(string model)
    {
        string clean = model.Trim();
        int slash = clean.IndexOf('/');
        if (slash > 0 && slash < clean.Length - 1)
            clean = clean[(slash + 1)..];
        return clean.Replace(':', '-').ToLowerInvariant();
    }

    /// <summary>
    /// Rebuilds the unpinned <c>@auto</c> aliases: one per model that at least two active
    /// providers serve, listing them best-first by the same (priority, provider order) rule the
    /// pinned catalog uses to settle cross-provider collisions.
    ///
    /// Built from the model-selection config rather than from live discovery so that the aliases
    /// exist before the first provider catalog fetch returns, and so they always agree with the
    /// <c>/api/tags</c> list, which is built from that same config.
    ///
    /// A model only one provider serves gets no alias: <c>@auto</c> would resolve to exactly the
    /// pinned entry, so publishing it would double the length of the model dropdown to say
    /// nothing new.
    /// </summary>
    private void RebuildAutoAliases(Dictionary<string, ProviderInfo> map, Dictionary<string, string> upstream, List<string> models)
    {
        Dictionary<string, List<AutoCandidate>> groups = new(StringComparer.OrdinalIgnoreCase);

        for (int order = 0; order < _providerRegistry.Providers.Count; order++)
        {
            ProviderInfo provider = _providerRegistry.Providers[order];
            foreach (ModelSelectionEntry entry in _modelSelectionStore.GetProviderModelSelections(provider.Name))
            {
                if (!entry.Enabled) continue;

                string key = AutoAliasKey(entry.Match);
                if (key.Length == 0) continue;

                string up = string.IsNullOrWhiteSpace(entry.Upstream) ? entry.Match : entry.Upstream;
                if (!groups.TryGetValue(key, out List<AutoCandidate>? list))
                {
                    list = [];
                    groups[key] = list;
                }
                list.Add(new AutoCandidate(provider, up, entry.Priority, order));
            }
        }

        Dictionary<string, List<(ProviderInfo Provider, string UpstreamModel)>> aliases = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, List<AutoCandidate> list) in groups)
        {
            // One attempt per provider. A provider listing the same model under both its bare and
            // vendor-prefixed id would otherwise be tried twice in a row, spending two candidates
            // to hit the identical failure.
            List<(ProviderInfo Provider, string UpstreamModel)> ordered = [.. list
                .GroupBy(c => c.Provider.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(c => c.Priority).ThenBy(c => c.UpstreamModel.Length).First())
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.Order)
                .Select(c => (c.Provider, c.UpstreamModel))];

            if (ordered.Count < 2) continue;

            string alias = $"{key}@{ProviderRegistry.AutoProviderToken}";
            aliases[alias] = ordered;

            // Register it as an ordinary model too, so /v1/models lists it, IsModelKnown accepts
            // it and the incidental single-provider lookups resolve to the preferred candidate.
            if (!map.ContainsKey(alias))
            {
                map[alias] = ordered[0].Provider;
                upstream[alias] = ordered[0].UpstreamModel;
                if (!models.Contains(alias)) models.Add(alias);
            }
        }

        _providerRegistry.UpdateAutoAliases(aliases);
    }

    private readonly record struct AutoCandidate(ProviderInfo Provider, string UpstreamModel, int Priority, int Order);

    private readonly record struct Claimant(ProviderInfo Provider, string UpstreamId, int Priority);

    private sealed class ProviderInfoNameComparer : IEqualityComparer<ProviderInfo>
    {
        public static readonly ProviderInfoNameComparer Instance = new();
        public bool Equals(ProviderInfo x, ProviderInfo y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(ProviderInfo obj) =>
            obj.Name is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
    }

    /// <summary>
    /// Non-chat models (embeddings, guardrails, rerankers, ASR/TTS, world models). The same
    /// keyword list the profile heuristic uses, exposed so the roster sync can refuse to
    /// auto-propose ids that could never answer a chat request.
    /// </summary>
    internal static bool IsNonChatModel(string model)
    {
        string m = model.ToLowerInvariant();
        return m.Contains("guard") || m.Contains("safety") || m.Contains("moderation") || m.Contains("embed") || m.Contains("retriever")
            || m.Contains("reranker") || m.Contains("reward") || m.Contains("parse") || m.Contains("detector")
            || m.Contains("clip") || m.Contains("nv-embed") || m.Contains("embedqa") || m.Contains("cached-model")
            || m.Contains("rerank") || m.Contains("classification") || m.Contains("riva-translate")
            || m.Contains("synthetic-video") || m.Contains("whisper") || m.Contains("tts") || m.Contains("cosmos");
    }

    internal (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] Capabilities, string Family) GetModelProfile(string model)
    {
        ModelExecutionConfig configured = _modelSelectionStore.GetExecutionConfigForModel(model, _providerRegistry.ModelToProvider);
        string m = model.ToLowerInvariant();
        bool tools = configured.SupportsTools ?? true;
        bool vision = configured.SupportsVision ?? (m.Contains("vision") || m.Contains("-vl") || m.Contains("neva") || m.Contains("vila") || m.Contains("fuyu") || m.Contains("kosmos"));
        int ctx, maxOut;

        if (IsNonChatModel(m))
        { ctx = 0; maxOut = 0; tools = false; }
        else if (m.Contains("deepseek")) { ctx = 1_000_000; maxOut = 384_000; }
        else if (m.Contains("nemotron-3-super")) { ctx = 1_000_000; maxOut = 16384; }
        else if (m.Contains("nemotron") && m.Contains("ultra")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("nemotron") || m.Contains("nvidia-nemotron")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("llama-4") || m.Contains("llama-3.3") || m.Contains("llama-3.2") || m.Contains("llama-3.1")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("llama-2") || m.Contains("codellama")) { ctx = 4096; maxOut = 4096; }
        else if (m.Contains("mistral-large-3") || m.Contains("mistral-large-2") || m.Contains("mistral-large")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("mistral") && (m.Contains("medium") || m.Contains("small"))) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("mixtral-8x22b")) { ctx = 65536; maxOut = 4096; }
        else if (m.Contains("mixtral") || m.Contains("mistral") || m.Contains("codestral") || m.Contains("ministral") || m.Contains("mistral-nemo")) { ctx = 32768; maxOut = 4096; }
        else if (m.Contains("qwen3-coder")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("qwen")) { ctx = 128_000; maxOut = 8192; }
        else if (m.Contains("gemma-4")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("gemma-3")) { ctx = 32768; maxOut = 8192; }
        else if (m.Contains("gemma-2") || m.Contains("gemma-2b") || m.Contains("codegemma")) { ctx = 8192; maxOut = 4096; }
        else if (m.Contains("phi-4")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("phi-3")) { ctx = 128_000; maxOut = 4096; }
        else if (m.Contains("granite-34b-code")) { ctx = 128_000; maxOut = 4096; }
        else if (m.Contains("granite")) { ctx = 128_000; maxOut = 4096; }
        else if (m.Contains("starcoder2")) { ctx = 16384; maxOut = 4096; }
        else if (m.Contains("gpt-oss")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("dbrx") || m.Contains("jamba")) { ctx = 32768; maxOut = 4096; }
        else if (m.Contains("yi-large") || m.Contains("seed-oss")) { ctx = 32768; maxOut = 4096; }
        else if (m.Contains("kimi")) { ctx = 128_000; maxOut = 8192; }
        else if (m.Contains("step-3")) { ctx = 128_000; maxOut = 16384; }
        else if (m.Contains("zai-glm")) { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("glm")) { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("minimax")) { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("cogito")) { ctx = 128_000; maxOut = 32768; }
        else if (m.Contains("solar") || m.Contains("zamba")) { ctx = 4096; maxOut = 4096; }
        else if (m.Contains("palmyra")) { ctx = 32768; maxOut = 4096; }
        else { ctx = 128_000; maxOut = 8192; }

        ctx = configured.ContextLength ?? ctx;
        maxOut = configured.MaxOutputTokens ?? maxOut;
        string[] capabilities = vision ? ["completion", "tools", "vision"] : ["completion", "tools"];
        string family = configured.Family
            ?? (m.Contains("deepseek") ? "deepseek"
            : m.Contains("nemotron") || m.Contains("llama-3.1-nemotron") || m.Contains("llama-3.3-nemotron") || m.Contains("nvidia-nemotron") || m.Contains("cosmos-reason") ? "nvidia"
            : m.Contains("llama") || m.Contains("codellama") ? "meta"
            : m.Contains("mistral") || m.Contains("mixtral") || m.Contains("codestral") || m.Contains("ministral") ? "mistralai"
            : m.Contains("qwen") ? "qwen"
            : m.Contains("gemma") || m.Contains("codegemma") ? "google"
            : m.Contains("phi-") ? "microsoft"
            : m.Contains("granite") ? "ibm"
            : m.Contains("gpt-oss") ? "openai"
            : m.Contains("nemotron") ? "nvidia"
            : _providerRegistry.ModelToProvider.TryGetValue(model, out ProviderInfo prov) ? prov.Name
            : "api");
        return (ctx, maxOut, tools, vision, capabilities, family);
    }

    internal CancellationTokenSource? CreateModelTimeoutCts(string model, CancellationToken outer)
    {
        var exec = _modelSelectionStore.GetExecutionConfigForModel(model, _providerRegistry.ModelToProvider);
        if (!exec.TimeoutSeconds.HasValue || exec.TimeoutSeconds.Value <= 0)
            return null;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        linked.CancelAfter(TimeSpan.FromSeconds(exec.TimeoutSeconds.Value));
        return linked;
    }

    internal ModelExecutionConfig GetExecutionConfigForModel(string model) =>
        _modelSelectionStore.GetExecutionConfigForModel(model, _providerRegistry.ModelToProvider);

    internal void EnsureDefaultModelPresent(CancellationToken ct)
    {
        string defaultModel = _providerRegistry.DefaultModel;
        if (AvailableModels.Any(m => string.Equals(m, defaultModel, StringComparison.OrdinalIgnoreCase)))
            return;

        foreach (ProviderInfo prov in _providerRegistry.Providers)
        {
            if (!_modelSelectionStore.IsPreferredModel(defaultModel, prov.Name))
                continue;

            var newMap = new Dictionary<string, ProviderInfo>(_providerRegistry.ModelToProvider, StringComparer.OrdinalIgnoreCase);
            var newUpstream = new Dictionary<string, string>(_providerRegistry.ModelToUpstream, StringComparer.OrdinalIgnoreCase);

            if (!newMap.ContainsKey(defaultModel))
            {
                newMap[defaultModel] = prov;
                newUpstream[defaultModel] = defaultModel;
            }
            string qualified = $"{defaultModel}@{prov.Name}";
            if (!newMap.ContainsKey(qualified))
            {
                newMap[qualified] = prov;
                newUpstream[qualified] = defaultModel;
            }
            _providerRegistry.UpdateModelMappings(newMap, newUpstream);
            AvailableModels = [defaultModel, .. AvailableModels.Where(m => !string.Equals(m, defaultModel, StringComparison.OrdinalIgnoreCase))];
            return;
        }

        if (_providerRegistry.Providers.Count > 0)
        {
            var first = _providerRegistry.Providers[0];
            var newMap = new Dictionary<string, ProviderInfo>(_providerRegistry.ModelToProvider, StringComparer.OrdinalIgnoreCase);
            var newUpstream = new Dictionary<string, string>(_providerRegistry.ModelToUpstream, StringComparer.OrdinalIgnoreCase);
            newMap[defaultModel] = first;
            newUpstream[defaultModel] = defaultModel;
            _providerRegistry.UpdateModelMappings(newMap, newUpstream);
            AvailableModels = [defaultModel, .. AvailableModels.Where(m => !string.Equals(m, defaultModel, StringComparison.OrdinalIgnoreCase))];
        }
    }

    internal static async Task<string[]> TryGetModelsFromProvider(ProviderInfo provider, CancellationToken ct)
    {
        try
        {
            string modelsPath = provider.Capabilities.ModelsPath;
            using var resp = await provider.Client.GetAsync(modelsPath, ct);
            if (!resp.IsSuccessStatusCode) return [];
            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            return ExtractModels(doc.RootElement);
        }
        catch { return []; }
    }

    private static string[] ExtractModels(JsonElement root)
    {
        IEnumerable<JsonElement> items = [];
        if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            items = data.EnumerateArray();
        else if (root.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
            items = models.EnumerateArray();

        return items
            .Select(item =>
            {
                if (item.ValueKind == JsonValueKind.String) return item.GetString();
                if (item.ValueKind != JsonValueKind.Object) return null;
                if (item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String) return id.GetString();
                if (item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String) return name.GetString();
                if (item.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.String) return model.GetString();
                return null;
            })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}