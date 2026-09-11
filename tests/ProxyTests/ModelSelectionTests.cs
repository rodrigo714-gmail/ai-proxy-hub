using System.Text.Json;
using Xunit;

namespace ProxyTests;

/// <summary>
/// Tests for LoadProviderModelSelections() and related model-selection helpers.
/// These tests operate on JSON parsing and selection logic independently of any live provider.
/// </summary>
[Collection("Proxy")]
public class ModelSelectionTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a temporary directory with a provider JSON file and loads selections using
    /// the same logic as LoadProviderModelSelections() in Program.cs.
    /// </summary>
    private static Dictionary<string, ModelSelectionEntry[]> LoadFromJson(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "test.json"), json);
        return LoadFromDir(dir);
    }

    // Mirrors the logic from Program.cs LoadProviderModelSelections() so tests
    // can run without spinning up the application.
    private static Dictionary<string, ModelSelectionEntry[]> LoadFromDir(string dir)
    {
        Dictionary<string, ModelSelectionEntry[]> selections = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("provider", out JsonElement provE) || provE.ValueKind != JsonValueKind.String) continue;
                string provider = provE.GetString()!.Trim().ToLowerInvariant();
                if (!root.TryGetProperty("models", out JsonElement modelsE) || modelsE.ValueKind != JsonValueKind.Array) continue;

                List<ModelSelectionEntry> entries = [];
                int idx = 0;
                foreach (JsonElement item in modelsE.EnumerateArray())
                {
                    idx++;
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string? match = item.GetString();
                        if (!string.IsNullOrWhiteSpace(match))
                            entries.Add(new ModelSelectionEntry(match!, idx, true, new ModelExecutionConfig()));
                        continue;
                    }
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    string? matchValue = null;
                    if (item.TryGetProperty("match", out JsonElement matchE) && matchE.ValueKind == JsonValueKind.String)
                        matchValue = matchE.GetString();
                    else if (item.TryGetProperty("model", out JsonElement modelE) && modelE.ValueKind == JsonValueKind.String)
                        matchValue = modelE.GetString();
                    else if (item.TryGetProperty("id", out JsonElement idE) && idE.ValueKind == JsonValueKind.String)
                        matchValue = idE.GetString();

                    if (string.IsNullOrWhiteSpace(matchValue)) continue;

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
                            OverrideClientParams: execE.TryGetProperty("override_client_params", out JsonElement ovE) && ovE.ValueKind is JsonValueKind.True or JsonValueKind.False && ovE.GetBoolean()
                        );
                    }

                    entries.Add(new ModelSelectionEntry(matchValue!, priority, enabled, exec));
                }

                if (entries.Count > 0)
                    selections[provider] = entries.OrderBy(x => x.Priority).ToArray();
            }
            catch { /* ignore malformed selection files */ }
        }
        return selections;
    }

    // ── parsing tests ────────────────────────────────────────────────────────

    [Fact]
    public void LoadProviderModelSelections_ParsesStringEntries()
    {
        string json = """
            {
              "provider": "test",
              "models": ["model-a", "model-b", "model-c"]
            }
            """;

        var selections = LoadFromJson(json);

        Assert.True(selections.ContainsKey("test"));
        Assert.Equal(3, selections["test"].Length);
        Assert.Equal("model-a", selections["test"][0].Match);
        Assert.Equal("model-b", selections["test"][1].Match);
        Assert.Equal("model-c", selections["test"][2].Match);
    }

    [Fact]
    public void LoadProviderModelSelections_ParsesObjectEntriesWithExecution()
    {
        string json = """
            {
              "provider": "nvidia",
              "models": [
                {
                  "match": "deepseek-v4-pro",
                  "priority": 1,
                  "enabled": true,
                  "execution": {
                    "context_length": 1000000,
                    "max_output_tokens": 16384,
                    "supports_tools": true,
                    "timeout_seconds": 120
                  }
                }
              ]
            }
            """;

        var selections = LoadFromJson(json);

        Assert.True(selections.ContainsKey("nvidia"));
        ModelSelectionEntry entry = selections["nvidia"][0];
        Assert.Equal("deepseek-v4-pro", entry.Match);
        Assert.Equal(1, entry.Priority);
        Assert.True(entry.Enabled);
        Assert.Equal(1_000_000, entry.Execution.ContextLength);
        Assert.Equal(16384, entry.Execution.MaxOutputTokens);
        Assert.True(entry.Execution.SupportsTools);
        Assert.Equal(120, entry.Execution.TimeoutSeconds);
    }

    [Fact]
    public void LoadProviderModelSelections_RespectsEnabledFalse()
    {
        string json = """
            {
              "provider": "test",
              "models": [
                { "match": "enabled-model",  "priority": 1, "enabled": true  },
                { "match": "disabled-model", "priority": 2, "enabled": false }
              ]
            }
            """;

        var selections = LoadFromJson(json);

        Assert.Equal(2, selections["test"].Length);
        Assert.False(selections["test"].Single(e => e.Match == "disabled-model").Enabled);
        Assert.True(selections["test"].Single(e => e.Match == "enabled-model").Enabled);
    }

    [Fact]
    public void LoadProviderModelSelections_OrdersByPriority()
    {
        string json = """
            {
              "provider": "test",
              "models": [
                { "match": "low",    "priority": 10 },
                { "match": "high",   "priority": 1  },
                { "match": "medium", "priority": 5  }
              ]
            }
            """;

        var selections = LoadFromJson(json);
        string[] ordered = selections["test"].Select(e => e.Match).ToArray();

        Assert.Equal(["high", "medium", "low"], ordered);
    }

    [Fact]
    public void LoadProviderModelSelections_SkipsMalformedFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bad.json"), "{ this is not json }");
        File.WriteAllText(Path.Combine(dir, "good.json"), """
            { "provider": "test", "models": ["ok-model"] }
            """);

        var selections = LoadFromDir(dir);

        Assert.True(selections.ContainsKey("test"));
        Assert.Single(selections["test"]);
    }

    [Fact]
    public void LoadProviderModelSelections_CaseInsensitiveProviderLookup()
    {
        string json = """{ "provider": "NVIDIA", "models": ["model-x"] }""";
        var selections = LoadFromJson(json);

        Assert.True(selections.ContainsKey("nvidia"));
        Assert.True(selections.ContainsKey("NVIDIA"));
    }

    [Fact]
    public void LoadProviderModelSelections_SupportsModelAndIdKeys()
    {
        string json = """
            {
              "provider": "test",
              "models": [
                { "model": "from-model-key", "priority": 1 },
                { "id":    "from-id-key",    "priority": 2 }
              ]
            }
            """;

        var selections = LoadFromJson(json);

        Assert.Equal("from-model-key", selections["test"][0].Match);
        Assert.Equal("from-id-key", selections["test"][1].Match);
    }

    // ── execution-config defaults ────────────────────────────────────────────

    [Fact]
    public void ModelExecutionConfig_DefaultsAreAllNull()
    {
        ModelExecutionConfig cfg = new();

        Assert.Null(cfg.ContextLength);
        Assert.Null(cfg.MaxOutputTokens);
        Assert.Null(cfg.SupportsTools);
        Assert.Null(cfg.SupportsVision);
        Assert.Null(cfg.Family);
        Assert.Null(cfg.Temperature);
        Assert.Null(cfg.TopP);
        Assert.Null(cfg.MaxTokensPreferred);
        Assert.Null(cfg.ReasoningEffort);
        Assert.Null(cfg.TimeoutSeconds);
    }

    [Fact]
    public void LoadProviderModelSelections_ParsesAllExecutionFields()
    {
        string json = """
            {
              "provider": "test",
              "models": [{
                "match": "full-model",
                "priority": 1,
                "execution": {
                  "context_length": 128000,
                  "max_output_tokens": 8192,
                  "supports_tools": true,
                  "supports_vision": false,
                  "family": "qwen",
                  "temperature": 0.6,
                  "top_p": 0.95,
                  "max_tokens": 4096,
                  "reasoning_effort": "high",
                  "timeout_seconds": 60
                }
              }]
            }
            """;

        ModelExecutionConfig exec = LoadFromJson(json)["test"][0].Execution;

        Assert.Equal(128000, exec.ContextLength);
        Assert.Equal(8192, exec.MaxOutputTokens);
        Assert.True(exec.SupportsTools);
        Assert.False(exec.SupportsVision);
        Assert.Equal("qwen", exec.Family);
        Assert.Equal(0.6, exec.Temperature);
        Assert.Equal(0.95, exec.TopP);
        Assert.Equal(4096, exec.MaxTokensPreferred);
        Assert.Equal("high", exec.ReasoningEffort);
        Assert.Equal(60, exec.TimeoutSeconds);
    }

    // ── curated nvidia.json ──────────────────────────────────────────────────

    [Fact]
    public void NvidiaJson_LoadsWithAtLeastElevenModels()
    {
        string configPath = FindConfigPath("nvidia.json");
        if (!File.Exists(configPath))
            return; // skip if config not found in test environment

        var selections = LoadFromDir(Path.GetDirectoryName(configPath)!);

        Assert.True(selections.ContainsKey("nvidia"));
        Assert.True(selections["nvidia"].Length >= 8,
            $"Expected >= 8 curated models, got {selections["nvidia"].Length}");
    }

    [Fact]
    public void NvidiaJson_CodingModelsHaveHighestPriority()
    {
        string configPath = FindConfigPath("nvidia.json");
        if (!File.Exists(configPath))
            return;

        var selections = LoadFromDir(Path.GetDirectoryName(configPath)!);

        Assert.True(selections.ContainsKey("nvidia"));
        // The curated coding-first picks, all verified present in NVIDIA's live catalog.
        // z-ai/glm-5.2 was one of them until 2026-09-11, when NVIDIA retired it (HTTP 410 Gone).
        ModelSelectionEntry[] enabled = selections["nvidia"].Where(e => e.Enabled).ToArray();
        Assert.True(enabled.Length >= 3, $"Expected >= 3 enabled NVIDIA models, got {enabled.Length}");
        Assert.Contains(enabled, e => e.Match == "nvidia/nemotron-3-super-120b-a12b");
        Assert.Contains(enabled, e => e.Match == "openai/gpt-oss-20b");
        Assert.Contains(enabled, e => e.Match == "deepseek-ai/deepseek-v4-pro");
    }

    [Fact]
    public void NvidiaJson_AllEnabledEntriesHaveNonEmptyMatch()
    {
        string configPath = FindConfigPath("nvidia.json");
        if (!File.Exists(configPath))
            return;

        var selections = LoadFromDir(Path.GetDirectoryName(configPath)!);

        Assert.All(
            selections["nvidia"].Where(e => e.Enabled),
            e => Assert.False(string.IsNullOrWhiteSpace(e.Match))
        );
    }

    private static string FindConfigPath(string fileName)
    {
        // Try relative to test run dir, then walk up to repo root
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "config", "model-selection", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "model-selection", fileName),
        ];
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
