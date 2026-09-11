using System.Text.Json;

namespace ProxyTests;

public class ModelSelectionStoreTests
{
    [Fact]
    public void ProviderModelSelections_HasAllProviders()
    {
        ModelSelectionStore store = new();
        var providers = store.ProviderModelSelections;
        Assert.True(providers.Count >= 9, $"Expected at least 9 providers, got {providers.Count}");
    }

    [Fact]
    public void ProviderModelSelections_HasCerebrasProvider()
    {
        ModelSelectionStore store = new();
        Assert.True(store.ProviderModelSelections.ContainsKey("cerebras"));
    }

    [Fact]
    public void GetProviderModelSelections_DeepSeek_ReturnsEntries()
    {
        ModelSelectionStore store = new();
        var entries = store.GetProviderModelSelections("deepseek");
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void GetProviderModelSelections_OpenAI_ReturnsEntries()
    {
        ModelSelectionStore store = new();
        var entries = store.GetProviderModelSelections("openai");
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void GetProviderModelSelections_Cerebras_ReturnsEntries()
    {
        ModelSelectionStore store = new();
        var entries = store.GetProviderModelSelections("cerebras");
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void GetProviderModelSelections_Ollama_MergedFromBothFiles()
    {
        ModelSelectionStore store = new();
        var entries = store.GetProviderModelSelections("ollama");
        Assert.True(entries.Length > 5, $"Expected merged entries, got {entries.Length}");
    }

    /// <summary>
    /// Ollama Cloud ids can embed a colon ("gpt-oss:120b"); the store must match them whole.
    /// </summary>
    [Fact]
    public void FindModelSelectionEntry_Ollama_GptOss120b_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("gpt-oss:120b", "ollama");

        Assert.NotNull(entry);
        Assert.Equal("gpt-oss:120b", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Ollama_Nemotron3Super_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("nemotron-3-super", "ollama");

        Assert.NotNull(entry);
        Assert.Equal("nemotron-3-super", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Ollama_KimiK27Code_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("kimi-k2.7-code", "ollama");

        Assert.NotNull(entry);
        Assert.Equal("kimi-k2.7-code", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Ollama_DeepSeekV4Pro_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("deepseek-v4-pro", "ollama");

        Assert.NotNull(entry);
        Assert.Equal("deepseek-v4-pro", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Ollama_Glm52_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("glm-5.2", "ollama");

        Assert.NotNull(entry);
        Assert.Equal("glm-5.2", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_DeepSeekV4Pro_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("deepseek-v4-pro", "deepseek");

        Assert.NotNull(entry);
        Assert.Equal("deepseek-v4-pro", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Cerebras_ZaiGlm47_ReturnsNull()
    {
        // zai-glm-4.7 was retired on 2026-09-11 (HTTP 404 model_archived_error, verified live).
        // A disabled entry must not resolve - FindModelSelectionEntry skips enabled:false.
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("zai-glm-4.7", "cerebras");

        Assert.Null(entry);
    }

    [Fact]
    public void FindModelSelectionEntry_Cerebras_GptOss120b_FindsEntry()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("gpt-oss-120b", "cerebras");

        Assert.NotNull(entry);
        Assert.Equal("gpt-oss-120b", entry.Value.Match);
    }

    [Fact]
    public void FindModelSelectionEntry_Moonshot_KimiK26_HasTemperature10()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("kimi-k2.6", "moonshot");

        Assert.NotNull(entry);
        Assert.Equal(1.0, entry.Value.Execution.Temperature);
    }

    [Fact]
    public void FindModelSelectionEntry_NonExistent_ReturnsNull()
    {
        ModelSelectionStore store = new();

        ModelSelectionEntry? entry = store.FindModelSelectionEntry("nonexistent-model", "nobody");

        Assert.Null(entry);
    }

    [Fact]
    public void GetExecutionConfigForModel_OllamaCloud_GptOss120b_HasContextLength()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("gpt-oss:120b", registry.ModelToProvider);

        Assert.True(config.ContextLength.HasValue);
    }

    [Fact]
    public void GetExecutionConfigForModel_OllamaCloud_KimiK26_HasOverrideClientParams()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("kimi-k2.6", registry.ModelToProvider);

        // kimi-k2.6 served via Ollama Cloud inherits the moonshot override_client_params
        // rule and must always pin temperature=1.0.
        Assert.True(config.Temperature.HasValue);
        Assert.Equal(1.0, config.Temperature.Value);
        Assert.True(config.OverrideClientParams);
    }

    [Fact]
    public void GetExecutionConfigForModel_OllamaCloud_Nemotron3Super_HasLowTemperature()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("nemotron-3-super", registry.ModelToProvider);

        Assert.True(config.Temperature.HasValue);
        Assert.Equal(0.2, config.Temperature.Value);
    }

    [Fact]
    public void IsPreferredModel_OllamaCloud_GptOss120b_ReturnsTrue()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("gpt-oss:120b", "ollama");

        Assert.True(isPreferred);
    }

    [Fact]
    public void IsPreferredModel_OllamaCloud_Nemotron3Super_ReturnsTrue()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("nemotron-3-super", "ollama");

        Assert.True(isPreferred);
    }

    [Fact]
    public void IsPreferredModel_Cerebras_ZaiGlm47_ReturnsFalse()
    {
        // Retired 2026-09-11 (HTTP 404 archived, verified live): a disabled entry is not preferred.
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("zai-glm-4.7", "cerebras");

        Assert.False(isPreferred);
    }

    [Fact]
    public void IsPreferredModel_DeepSeekV4Pro_ReturnsTrue()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("deepseek-v4-pro", "deepseek");

        Assert.True(isPreferred);
    }

    [Fact]
    public void IsPreferredModel_NonExistent_ReturnsFalse()
    {
        ModelSelectionStore store = new();

        bool isPreferred = store.IsPreferredModel("nonexistent", "nobody");

        Assert.False(isPreferred);
    }

    [Fact]
    public void GetPreferredModelPriority_DeepSeekV4Pro_ReturnsPriority()
    {
        ModelSelectionStore store = new();

        int priority = store.GetPreferredModelPriority("deepseek-v4-pro", "deepseek");

        Assert.True(priority > 0);
    }

    [Fact]
    public void GetPreferredModelPriority_OllamaCloud_KimiK26_ReturnsExpectedPriority()
    {
        ModelSelectionStore store = new();

        int priority = store.GetPreferredModelPriority("kimi-k2.6", "ollama");

        // kimi-k2.6 is somewhere in ollamacloud.json
        Assert.True(priority > 0);
    }

    [Fact]
    public void GetExecutionConfigForModel_HasTemperature()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        // "deepseek-v4-pro" in deepseek.json has temperature 0.2
        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);
        Assert.True(config.Temperature.HasValue);
    }

    [Fact]
    public void GetExecutionConfigForModel_DeepSeekV4Pro_HasMaxOutputTokens()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);
        Assert.True(config.MaxOutputTokens.HasValue);
    }

    [Fact]
    public void GetExecutionConfigForModel_DeepSeekV4Pro_HasContextLength()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-pro", registry.ModelToProvider);
        Assert.True(config.ContextLength.HasValue);
    }

    [Fact]
    public void GetExecutionConfigForModel_DeepSeekFlash_HasReasoningEffort()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("deepseek-v4-flash", registry.ModelToProvider);
        Assert.NotNull(config.ReasoningEffort);
        Assert.NotEmpty(config.ReasoningEffort);
    }

    [Fact]
    public void GetExecutionConfigForModel_UnknownModel_ReturnsDefaults()
    {
        ModelSelectionStore store = new();
        ProviderHttpClientFactory factory = new();
        ProviderRegistry registry = new(factory);

        ModelExecutionConfig config = store.GetExecutionConfigForModel("model-that-definitely-does-not-exist-12345", registry.ModelToProvider);
        Assert.NotNull(config);
    }
}