using System.Text.Json;

internal sealed class ModelSelectionStore
{
    private Dictionary<string, ModelSelectionEntry[]> _providerModelSelections;
    private readonly string? _selectionDirOverride;

    public ModelSelectionStore() : this(null) { }

    /// <param name="selectionDirOverride">
    /// Test seam: load from exactly this directory instead of probing the default locations,
    /// so a test can control the roster without touching the workspace config.
    /// </param>
    internal ModelSelectionStore(string? selectionDirOverride)
    {
        _selectionDirOverride = selectionDirOverride;
        _providerModelSelections = LoadProviderModelSelections(_selectionDirOverride);
    }

    internal Dictionary<string, ModelSelectionEntry[]> ProviderModelSelections => _providerModelSelections;

    /// <summary>
    /// Re-reads config/model-selection/*.json from disk and swaps the in-memory table
    /// atomically (reference assignment; readers either see the old or the new map,
    /// never a half-loaded one). Used by the roster sync after it writes config changes,
    /// so a renewed roster takes effect without a restart.
    /// </summary>
    internal void Reload()
    {
        _providerModelSelections = LoadProviderModelSelections(_selectionDirOverride);
    }

    /// <summary>The config/model-selection directory the store loads from, or null if none exists.</summary>
    internal static string? ResolveSelectionDirectory()
    {
        string[] candidateDirs =
        [
            Path.Combine(AppContext.BaseDirectory, "config", "model-selection"),
            Path.Combine(Directory.GetCurrentDirectory(), "config", "model-selection")
        ];

        return candidateDirs.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Finds the JSON file that declares the given provider (the filename is cosmetic —
    /// the "provider" field inside binds it). Returns the first match when several files
    /// declare the same provider.
    /// </summary>
    internal static string? FindProviderSelectionFile(string providerName)
    {
        string? dir = ResolveSelectionDirectory();
        if (dir is null)
        {
            return null;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("provider", out JsonElement provE)
                    && provE.ValueKind == JsonValueKind.String
                    && string.Equals(provE.GetString()?.Trim(), providerName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            catch
            {
                // malformed file — the loader ignores it too
            }
        }

        return null;
    }

    internal ModelExecutionConfig GetExecutionConfigForModel(string model, IReadOnlyDictionary<string, ProviderInfo> modelToProvider)
    {
        // Collect every (provider, entry) that matches this model, then pick the
        // one with the longest match substring (= most specific). This handles
        // cases where two providers both match a given upstream id (e.g. ollama
        // matches "nemotron" and ollamacloud matches "nemotron-3-super"): the
        // longer match wins regardless of which JSON file is loaded first.
        ModelSelectionEntry best = default;
        string? bestProvider = null;
        bool hasBest = false;

        if (modelToProvider.TryGetValue(model, out ProviderInfo provider))
        {
            ModelSelectionEntry? entry = FindModelSelectionEntry(model, provider.Name);
            if (entry.HasValue)
            {
                best = entry.Value;
                bestProvider = provider.Name;
                hasBest = true;
            }
        }

        foreach (KeyValuePair<string, ModelSelectionEntry[]> kv in _providerModelSelections)
        {
            ModelSelectionEntry? entry = FindModelSelectionEntry(model, kv.Key);
            if (!entry.HasValue)
            {
                continue;
            }

            if (!hasBest || entry.Value.Match.Length > best.Match.Length)
            {
                best = entry.Value;
                bestProvider = kv.Key;
                hasBest = true;
            }
        }

        return hasBest ? best.Execution : new ModelExecutionConfig();
    }

    internal int GetPreferredModelPriority(string model, string providerName)
    {
        ModelSelectionEntry? entry = FindModelSelectionEntry(model, providerName);
        return entry?.Priority ?? int.MaxValue;
    }

    internal bool IsPreferredModel(string model, string providerName) =>
        FindModelSelectionEntry(model, providerName) != null;

    internal ModelSelectionEntry[] GetProviderModelSelections(string providerName)
    {
        if (_providerModelSelections.TryGetValue(providerName, out ModelSelectionEntry[]? configured) && configured.Length > 0)
        {
            return configured;
        }

        return GetDefaultPreferredModelSelections();
    }

    internal ModelSelectionEntry? FindModelSelectionEntry(string model, string providerName)
    {
        string m = model.ToLowerInvariant();

        if (m.Contains("guard") || m.Contains("safety") || m.Contains("embed") || m.Contains("retriever") || m.Contains("reranker")
            || m.Contains("reward") || m.Contains("parse") || m.Contains("detector") || m.Contains("clip") || m.Contains("riva-translate"))
        {
            return null;
        }

        // Specificity beats priority: entries are matched as substrings, so a generic
        // entry ("gpt-5.4", priority 3) would otherwise shadow the specific one
        // ("gpt-5.4-mini", priority 5) and hand it the wrong context window.
        // Longest match first, priority only as a tie-break.
        ModelSelectionEntry[] entries = GetProviderModelSelections(providerName);
        foreach (ModelSelectionEntry entry in entries.OrderByDescending(x => x.Match.Length).ThenBy(x => x.Priority))
        {
            if (!entry.Enabled)
            {
                continue;
            }

            if (m.Contains(entry.Match, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static Dictionary<string, ModelSelectionEntry[]> LoadProviderModelSelections(string? dirOverride = null)
    {
        Dictionary<string, ModelSelectionEntry[]> selections = new(StringComparer.OrdinalIgnoreCase);
        string[] candidateDirs = dirOverride is not null
            ? [dirOverride]
            :
            [
                Path.Combine(AppContext.BaseDirectory, "config", "model-selection"),
                Path.Combine(Directory.GetCurrentDirectory(), "config", "model-selection")
            ];

        foreach (string dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            // Deterministic file order across platforms. Directory.EnumerateFiles returns
            // alphabetically on NTFS (Windows) but in arbitrary inode order on ext4 (Linux CI),
            // and two files may declare a provider whose model ids collide at equal match length
            // (e.g. nvidia.json and openrouter.json both list "nvidia/nemotron-3-super-120b-a12b").
            // The loader's longest-match tie-break keeps the first entry seen on equal length, so
            // without an explicit sort the winning provider — and the config a test observes —
            // depends on the filesystem. Ordinal sort makes Linux match Windows.
            foreach (string file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                    JsonElement root = doc.RootElement;

                    if (!root.TryGetProperty("provider", out JsonElement provE) || provE.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string provider = provE.GetString()!.Trim().ToLowerInvariant();

                    if (!root.TryGetProperty("models", out JsonElement modelsE) || modelsE.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    List<ModelSelectionEntry> entries = [];
                    int idx = 0;
                    foreach (JsonElement item in modelsE.EnumerateArray())
                    {
                        idx++;
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string? match = item.GetString();
                            if (!string.IsNullOrWhiteSpace(match))
                            {
                                entries.Add(new ModelSelectionEntry(match!, idx, true, new ModelExecutionConfig()));
                            }

                            continue;
                        }

                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        string? matchValue = null;
                        if (item.TryGetProperty("match", out JsonElement matchE) && matchE.ValueKind == JsonValueKind.String)
                        {
                            matchValue = matchE.GetString();
                        }
                        else if (item.TryGetProperty("model", out JsonElement modelE) && modelE.ValueKind == JsonValueKind.String)
                        {
                            matchValue = modelE.GetString();
                        }
                        else if (item.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                        {
                            matchValue = idE.GetString();
                        }

                        if (string.IsNullOrWhiteSpace(matchValue))
                        {
                            continue;
                        }

                        int priority = item.TryGetProperty("priority", out JsonElement priE) && priE.ValueKind == JsonValueKind.Number
                            ? priE.GetInt32() : idx;
                        bool enabled = !item.TryGetProperty("enabled", out JsonElement enE) || enE.ValueKind != JsonValueKind.False;

                        ModelExecutionConfig exec = new();
                        if (item.TryGetProperty("execution", out JsonElement execE) && execE.ValueKind == JsonValueKind.Object)
                        {
                            exec = new ModelExecutionConfig(
                                ContextLength: execE.TryGetProperty("context_length", out JsonElement ctxE) && ctxE.ValueKind == JsonValueKind.Number ? ctxE.GetInt32() : null,
                                MaxOutputTokens: execE.TryGetProperty("max_output_tokens", out JsonElement outE) && outE.ValueKind == JsonValueKind.Number ? outE.GetInt32() : null,
                                SupportsTools: execE.TryGetProperty("supports_tools", out JsonElement stE) && stE.ValueKind is JsonValueKind.True or JsonValueKind.False ? stE.GetBoolean() : null,
                                SupportsVision: execE.TryGetProperty("supports_vision", out JsonElement svE) && svE.ValueKind is JsonValueKind.True or JsonValueKind.False ? svE.GetBoolean() : null,
                                Family: execE.TryGetProperty("family", out JsonElement famE) && famE.ValueKind == JsonValueKind.String ? famE.GetString() : null,
                                Temperature: execE.TryGetProperty("temperature", out JsonElement tempE) && tempE.ValueKind == JsonValueKind.Number ? tempE.GetDouble() : null,
                                TopP: execE.TryGetProperty("top_p", out JsonElement topPE) && topPE.ValueKind == JsonValueKind.Number ? topPE.GetDouble() : null,
                                MaxTokensPreferred: execE.TryGetProperty("max_tokens", out JsonElement maxTokE) && maxTokE.ValueKind == JsonValueKind.Number ? maxTokE.GetInt32() : null,
                                ReasoningEffort: execE.TryGetProperty("reasoning_effort", out JsonElement reE) && reE.ValueKind == JsonValueKind.String ? reE.GetString() : null,
                                TimeoutSeconds: execE.TryGetProperty("timeout_seconds", out JsonElement timeoutE) && timeoutE.ValueKind == JsonValueKind.Number ? timeoutE.GetInt32() : null,
                                OverrideClientParams: execE.TryGetProperty("override_client_params", out JsonElement ovE) && ovE.ValueKind is JsonValueKind.True or JsonValueKind.False && ovE.GetBoolean(),
                                SupportsReasoning: execE.TryGetProperty("supports_reasoning", out JsonElement srE) && srE.ValueKind is JsonValueKind.True or JsonValueKind.False ? srE.GetBoolean() : null,
                                UsesMaxCompletionTokens: execE.TryGetProperty("uses_max_completion_tokens", out JsonElement mctE) && mctE.ValueKind is JsonValueKind.True or JsonValueKind.False && mctE.GetBoolean(),
                                SupportsTemperature: execE.TryGetProperty("supports_temperature", out JsonElement stmpE) && stmpE.ValueKind is JsonValueKind.True or JsonValueKind.False ? stmpE.GetBoolean() : null
                            );
                        }

                        string? upstream = item.TryGetProperty("upstream", out JsonElement upE) && upE.ValueKind == JsonValueKind.String
                            ? upE.GetString() : null;
                        // "_auto": true marks entries generated by the roster sync. The loader
                        // reads it so retirement logic can tell auto entries from curated ones;
                        // the sync itself reads the JSON files directly for round-tripping.
                        bool autoManaged = item.TryGetProperty("_auto", out JsonElement autoE)
                            && autoE.ValueKind == JsonValueKind.True;
                        // "_retired": true marks a disable that came from the sync rather than from
                        // the curator's hand or from a fresh addition awaiting review. Only such
                        // entries are re-enabled when the model reappears on the catalog.
                        bool retired = item.TryGetProperty("_retired", out JsonElement retE)
                            && retE.ValueKind == JsonValueKind.True;
                        // "_keep_enabled": true exempts the entry from catalog-absence retirement —
                        // for models that answer real requests while /v1/models no longer lists them.
                        bool keepEnabled = item.TryGetProperty("_keep_enabled", out JsonElement keE)
                            && keE.ValueKind == JsonValueKind.True;
                        entries.Add(new ModelSelectionEntry(matchValue!, priority, enabled, exec, upstream, autoManaged, retired, keepEnabled));
                    }

                    if (entries.Count > 0)
                    {
                        // Merge with existing entries for the same provider (multiple files may
                        // declare the same provider, e.g. ollama.json + ollamacloud.json both use
                        // "provider": "ollama"). Previously loaded entries are preserved.
                        if (selections.TryGetValue(provider, out ModelSelectionEntry[] existing))
                        {
                            // On a duplicate match string, the more useful entry wins:
                            // enabled beats disabled, then lower priority beats higher.
                            // Keeping whichever file happened to load first would silently
                            // disable models that another file explicitly enables.
                            List<ModelSelectionEntry> merged = [.. existing];
                            Dictionary<string, int> byMatch = new(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < merged.Count; i++)
                                byMatch.TryAdd(merged[i].Match, i);

                            foreach (ModelSelectionEntry entry in entries)
                            {
                                if (!byMatch.TryGetValue(entry.Match, out int at))
                                {
                                    byMatch[entry.Match] = merged.Count;
                                    merged.Add(entry);
                                    continue;
                                }

                                ModelSelectionEntry current = merged[at];
                                bool newWins = (entry.Enabled && !current.Enabled)
                                    || (entry.Enabled == current.Enabled && entry.Priority < current.Priority);
                                if (newWins)
                                    merged[at] = entry;
                            }
                            entries = merged;
                        }

                        // Order entries by (priority asc, match length desc). A longer match
                        // substring is more specific than a shorter one, so it wins when two
                        // entries (e.g. "nemotron" vs "nemotron-3-super") both match the
                        // same upstream id. Without this, a generic match would shadow a
                        // specific one whenever the generic file is loaded first.
                        selections[provider] = entries
                            .OrderBy(x => x.Priority)
                            .ThenByDescending(x => x.Match.Length)
                            .ToArray();
                    }
                }
                catch
                {
                    // ignore malformed selection files
                }
            }
        }

        return selections;
    }

    private static ModelSelectionEntry[] GetDefaultPreferredModelSelections() =>
    [
        new("deepseek-v4-pro", 1, true, new()),
        new("qwen3-coder-480b-a35b", 2, true, new()),
        new("qwen3.5-397b-a17b", 3, true, new()),
        new("mistral-large-3-675b-instruct-2512", 4, true, new()),
        new("llama-4-maverick-17b-128e-instruct", 5, true, new()),
        new("llama-3.1-nemotron-ultra-253b-v1", 6, true, new()),
        new("nemotron-4-340b-instruct", 7, true, new()),
        new("gpt-oss-120b", 8, true, new()),
        new("kimi-k2.6", 9, true, new()),
        new("llama-3.3-nemotron-super-49b-v1.5", 10, true, new())
    ];
}
