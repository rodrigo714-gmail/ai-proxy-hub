using System.Text.Json;
using Xunit;

namespace ProxyTests;

/// <summary>
/// Validates that ApplyExecutionDefaults injects the correct parameters
/// for every enabled model/provider declared in config/model-selection/*.json.
/// These tests are fully offline – no live API calls are made.
/// </summary>
[Collection("Proxy")]
public class ParameterValidationTests
{
    private static RequestTransformer CreateTransformer()
    {
        ProviderHttpClientFactory httpClientFactory = new();
        ProviderRegistry providerRegistry           = new(httpClientFactory);
        ModelSelectionStore modelSelectionStore     = new();
        ModelCatalogService modelCatalog            = new(providerRegistry, modelSelectionStore);
        ReasoningCacheService cache                 = new();
        return new(modelCatalog, cache);
    }

    private static ProviderCapabilities ResolveCaps(string providerName)
    {
        if (string.IsNullOrEmpty(providerName))
            return default;
        if (providerName.Equals("ollamacloud", StringComparison.OrdinalIgnoreCase))
            return ProviderCapabilitiesRegistry.Get("ollama");
        return ProviderCapabilitiesRegistry.TryGet(providerName, out ProviderCapabilities caps)
            ? caps
            : default;
    }

    private static JsonElement Transform(RequestTransformer sut, string model, string providerName = "")
    {
        string raw    = """{"model":"x","messages":[{"role":"user","content":"hi"}]}""";
        string result = sut.ApplyExecutionDefaults(raw, model, ResolveCaps(providerName));
        return JsonDocument.Parse(result).RootElement;
    }

    private static JsonElement TransformWithBody(RequestTransformer sut, string body, string model, string providerName = "")
    {
        string result = sut.ApplyExecutionDefaults(body, model, ResolveCaps(providerName));
        return JsonDocument.Parse(result).RootElement;
    }

    // ─── DeepSeek: v4-pro + v4-flash enabled (coder-6.7b disabled) ──────────

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek", true,  false)]
    [InlineData("deepseek-v4-flash", "deepseek", true,  false)]
    public void DeepSeek_ReasoningEffortPresenceMatchesModel(
        string model, string provider, bool expectReasoningEffort, bool expectTopP)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, provider);

        if (expectReasoningEffort)
            Assert.True(result.TryGetProperty("reasoning_effort", out _),
                $"{model}: reasoning_effort should be injected for DeepSeek reasoning models");
        else
            Assert.False(result.TryGetProperty("reasoning_effort", out _),
                $"{model}: reasoning_effort should NOT be injected for non-reasoning models");

        if (expectTopP)
            Assert.True(result.TryGetProperty("top_p", out _),
                $"{model}: top_p should be present for non-reasoning models");
        else
            Assert.False(result.TryGetProperty("top_p", out _),
                $"{model}: top_p must NOT be present alongside reasoning_effort (DeepSeek docs)");
    }

    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4-flash")]
    public void DeepSeek_ReasoningModels_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "deepseek");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"{model}: max_tokens should be injected");
        Assert.True(maxTok.GetInt32() > 0,
            $"{model}: max_tokens must be a positive integer");
    }

    // ─── NVIDIA NIM ──────────────────────────────────────────────────────

    public static TheoryData<string> NvidiaModels =>
    [
        "nvidia/nemotron-3-super-120b-a12b",
        "z-ai/glm-5.2",
        "deepseek-ai/deepseek-v4-pro",
        "openai/gpt-oss-120b",
        "nvidia/nemotron-3-ultra-550b-a55b"
    ];

    [Theory]
    [MemberData(nameof(NvidiaModels))]
    public void Nvidia_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "nvidia");

        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"NVIDIA/{model}: reasoning_effort must NOT be sent (not supported by NVIDIA NIM API)");
    }

    [Theory]
    [MemberData(nameof(NvidiaModels))]
    public void Nvidia_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "nvidia");

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"NVIDIA/{model}: max_tokens should be injected from config");
        Assert.True(maxTok.GetInt32() > 0,
            $"NVIDIA/{model}: max_tokens must be positive");
    }

    // ─── OpenAI ──────────────────────────────────────────────────────────

    /// <summary>
    /// GPT-5.x and the o-series answer 400 "Unsupported parameter: 'max_tokens'".
    /// The budget must go out as max_completion_tokens, and max_tokens must be absent.
    /// </summary>
    [Theory]
    [InlineData("gpt-5.5-pro")]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.4")]
    [InlineData("gpt-5.4-mini")]
    [InlineData("o4-mini")]
    public void OpenAI_Models_MaxCompletionTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");

        Assert.True(result.TryGetProperty("max_completion_tokens", out JsonElement maxTok),
            $"OpenAI/{model}: max_completion_tokens should be injected");
        Assert.True(maxTok.GetInt32() > 0,
            $"OpenAI/{model}: max_completion_tokens must be positive");
        Assert.False(result.TryGetProperty("max_tokens", out _),
            $"OpenAI/{model}: max_tokens must NOT be sent (rejected by the OpenAI API)");
    }

    /// <summary>
    /// The same models also reject an explicit temperature/top_p.
    /// </summary>
    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.4")]
    [InlineData("gpt-5.4-mini")]
    [InlineData("o4-mini")]
    public void OpenAI_Models_SamplingParamsStripped(string model)
    {
        RequestTransformer sut = CreateTransformer();
        string body = """{"model":"x","messages":[],"temperature":0.7,"top_p":0.5}""";
        JsonElement result = TransformWithBody(sut, body, model, "openai");

        Assert.False(result.TryGetProperty("temperature", out _),
            $"OpenAI/{model}: temperature must NOT be sent");
        Assert.False(result.TryGetProperty("top_p", out _),
            $"OpenAI/{model}: top_p must NOT be sent");
    }

    [Theory]
    [InlineData("o4-mini")]
    public void OpenAI_ReasoningCapableModels_ReasoningEffortInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");
        Assert.True(result.TryGetProperty("reasoning_effort", out _),
            $"OpenAI/{model}: reasoning_effort should be injected");
    }

    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.5-pro")]
    public void OpenAI_Gpt55_NoReasoningEffortInjected_IncompatibleWithTools(string model)
    {
        // OpenAI answers 400 "Function tools with reasoning_effort are not supported for
        // gpt-5.5" the moment an agent-mode client (VS 2026 Copilot) sends tools, so the
        // config deliberately leaves reasoning_effort unset for the gpt-5.5 family.
        // (gpt-5.5-pro resolves to the enabled gpt-5.5 entry for injection purposes.)
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");
        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"OpenAI/{model}: reasoning_effort must NOT be injected — OpenAI rejects it combined with tools");
    }

    [Theory]
    [InlineData("gpt-5.4")]
    [InlineData("gpt-5.4-mini")]
    public void OpenAI_NonReasoningModels_NoReasoningEffort(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");
        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"OpenAI/{model}: reasoning_effort must NOT be injected");
    }

    [Theory]
    [InlineData("gpt-5.5-pro")]
    [InlineData("gpt-5.5")]
    [InlineData("o4-mini")]
    public void OpenAI_ReasoningModels_NoTopPAlongReasoningEffort(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openai");

        bool hasRE = result.TryGetProperty("reasoning_effort", out _);
        bool hasTP = result.TryGetProperty("top_p", out _);

        if (hasRE)
            Assert.False(hasTP,
                $"OpenAI/{model}: top_p must NOT be sent when reasoning_effort is active");
    }

    // ─── Groq ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("qwen/qwen3.6-27b")]
    [InlineData("openai/gpt-oss-120b")]
    [InlineData("meta-llama/llama-4-scout-17b-16e-instruct")]
    [InlineData("llama-3.3-70b-versatile")]
    [InlineData("qwen/qwen3-32b")]
    [InlineData("openai/gpt-oss-20b")]
    [InlineData("groq/compound")]
    public void Groq_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "groq");
        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"Groq/{model}: reasoning_effort must NOT be sent (not supported by Groq API)");
    }

    [Theory]
    [InlineData("qwen/qwen3.6-27b")]
    [InlineData("openai/gpt-oss-120b")]
    [InlineData("groq/compound-mini")]
    [InlineData("llama-3.3-70b-versatile")]
    [InlineData("llama-3.1-8b-instant")]
    [InlineData("openai/gpt-oss-20b")]
    [InlineData("groq/compound")]
    public void Groq_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "groq");
        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"Groq/{model}: max_tokens should be injected from config");
        Assert.True(maxTok.GetInt32() > 0,
            $"Groq/{model}: max_tokens must be positive");
    }

    // ─── Ollama Cloud ────────────────────────────────────────────────────

    [Theory]
    [InlineData("gpt-oss:120b")]
    [InlineData("nemotron-3-super")]
    [InlineData("kimi-k2.7-code")]
    public void OllamaCloud_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "ollamacloud");
        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"OllamaCloud/{model}: max_tokens should be injected from config");
        Assert.True(maxTok.GetInt32() > 0,
            $"OllamaCloud/{model}: max_tokens must be positive");
    }

    [Theory]
    [InlineData("qwen3-coder:480b")]
    [InlineData("devstral-2:123b")]
    [InlineData("kimi-k2.6")]
    public void OllamaCloud_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "ollamacloud");
        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"OllamaCloud/{model}: reasoning_effort must NOT be sent");
    }

    // ─── OpenRouter ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("nvidia/nemotron-3-super-120b-a12b")]
    [InlineData("qwen/qwen3-coder")]
    public void OpenRouter_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "openrouter");
        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"OpenRouter/{model}: reasoning_effort must NOT be sent");
    }

    // ─── Moonshot / Kimi ────────────────────────────────────────────────

    [Theory]
    [InlineData("kimi-k2.7-code")]
    [InlineData("kimi-k2.6")]
    [InlineData("moonshot-v1-128k")]
    [InlineData("moonshot-v1-32k")]
    public void Moonshot_Models_MaxTokensInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "moonshot");
        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"Moonshot/{model}: max_tokens should be injected from config");
        Assert.True(maxTok.GetInt32() > 0,
            $"Moonshot/{model}: max_tokens must be positive");
    }

    [Theory]
    [InlineData("moonshot-v1-128k")]
    [InlineData("moonshot-v1-auto")]
    [InlineData("moonshot-v1-32k")]
    public void Moonshot_Models_NoReasoningEffortLeakage(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "moonshot");
        Assert.False(result.TryGetProperty("reasoning_effort", out _),
            $"Moonshot/{model}: reasoning_effort must NOT be sent");
    }

    [Theory]
    [InlineData("kimi-k2.7-code")]
    [InlineData("kimi-k2.6")]
    [InlineData("moonshot-v1-128k")]
    public void Moonshot_Models_TemperatureInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "moonshot");
        Assert.True(result.TryGetProperty("temperature", out JsonElement temp),
            $"Moonshot/{model}: temperature should be injected from config");
        Assert.True(temp.GetDouble() > 0,
            $"Moonshot/{model}: temperature must be a positive value");
    }

    [Theory]
    [InlineData("moonshot-v1-128k")]
    [InlineData("moonshot-v1-32k")]
    public void Moonshot_Models_TopPInjected(string model)
    {
        RequestTransformer sut = CreateTransformer();
        JsonElement result = Transform(sut, model, "moonshot");
        Assert.True(result.TryGetProperty("top_p", out JsonElement topP),
            $"Moonshot/{model}: top_p should be injected from config");
        Assert.True(topP.GetDouble() > 0,
            $"Moonshot/{model}: top_p must be a positive value");
    }

    // ─── top_k filtering ────────────────────────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek")]
    [InlineData("deepseek-v4-flash", "deepseek")]
    [InlineData("gpt-5.5-pro",       "openai")]
    [InlineData("o4-mini",           "openai")]
    [InlineData("kimi-k2.7-code",    "moonshot")]
    [InlineData("kimi-k2.6",         "moonshot")]
    public void TopK_IsFiltered_ForNonSupportingProviders(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[{"role":"user","content":"hi"}],"top_k":50}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.False(result.TryGetProperty("top_k", out _),
            $"{provider}/{model}: top_k must be filtered out (not supported by {provider})");
    }

    [Theory]
    [InlineData("qwen/qwen3-coder-480b-a35b-instruct", "nvidia")]
    [InlineData("llama-3.3-70b-versatile",              "groq")]
    [InlineData("nvidia/nemotron-3-super-120b-a12b",    "openrouter")]
    public void TopK_IsPreserved_ForSupportingProviders(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[{"role":"user","content":"hi"}],"top_k":50}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("top_k", out JsonElement topK),
            $"{provider}/{model}: top_k should be preserved (supported by {provider})");
        Assert.Equal(50, topK.GetInt32());
    }

    // ─── Client-supplied values ─────────────────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek")]
    [InlineData("deepseek-v4-flash", "deepseek")]
    [InlineData("llama-3.3-70b-versatile", "groq")]
    [InlineData("moonshot-v1-128k",  "moonshot")]
    public void ClientSupplied_MaxTokens_IsNotOverridden(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"max_tokens":99}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("max_tokens", out JsonElement maxTok),
            $"{provider}/{model}: max_tokens field must be present");
        Assert.Equal(99, maxTok.GetInt32());
    }

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek")]
    [InlineData("gpt-5.5-pro",       "openai")]
    public void ClientSupplied_ReasoningEffort_IsNotOverridden(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"reasoning_effort":"low"}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("reasoning_effort", out JsonElement re),
            $"{provider}/{model}: reasoning_effort must be present");
        Assert.Equal("low", re.GetString());
    }

    [Theory]
    [InlineData("qwen/qwen3.5-397b-a17b",        "nvidia")]
    [InlineData("llama-3.3-70b-versatile",        "groq")]
    [InlineData("moonshot-v1-128k",              "moonshot")]
    public void ClientSupplied_Temperature_IsNotOverridden(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"temperature":0.99}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("temperature", out JsonElement temp),
            $"{provider}/{model}: temperature must be present");
        Assert.Equal(0.99, temp.GetDouble(), precision: 5);
    }

    [Theory]
    [InlineData("nvidia/nemotron-3-super-120b-a12b", "nvidia")]
    [InlineData("llama-3.3-70b-versatile",            "groq")]
    public void ClientSupplied_TopK_IsNotOverridden_ForSupportingProviders(string model, string provider)
    {
        RequestTransformer sut = CreateTransformer();
        string body   = """{"model":"x","messages":[],"top_k":42}""";
        JsonElement result = TransformWithBody(sut, body, model, provider);

        Assert.True(result.TryGetProperty("top_k", out JsonElement topK),
            $"{provider}/{model}: top_k must be present");
        Assert.Equal(42, topK.GetInt32());
    }

    // ─── Context-window config completeness ─────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro",              1_048_576, 384_000)]
    [InlineData("deepseek-v4-flash",            1_048_576, 131_072)]
    [InlineData("moonshotai/kimi-k2.6",          262_144, 262_144)]
    [InlineData("nvidia/nemotron-3-super-120b-a12b", 1_000_000, 262_144)]
    [InlineData("gpt-5.5",       400_000, 128_000)]
    [InlineData("gpt-5.4",       400_000,  65_536)]
    [InlineData("gpt-5.4-mini",  200_000,  32_768)]
    [InlineData("o4-mini",       200_000, 100_000)]
    [InlineData("llama-3.3-70b-versatile",   131_072, 32_768)]
    [InlineData("openai/gpt-oss-20b",         131_072, 65_536)]
    [InlineData("kimi-k2.7-code",            262_144, 131_072)]
    [InlineData("kimi-k2.6",                 262_144, 131_072)]
    [InlineData("moonshot-v1-128k",          131_072,  32_768)]
    [InlineData("moonshot-v1-auto",          131_072,  32_768)]
    [InlineData("moonshot-v1-32k",            32_768,   8_192)]
    [InlineData("qwen/qwen3-coder",                  1_048_576, 262_000)]
    [InlineData("nvidia/llama-3.3-nemotron-super-49b-v1.5", 131_072, 16_384)]
    [InlineData("deepseek/deepseek-v4-pro",          1_048_576, 384_000)]
    // Cerebras caps this one at 8192 for messages and completion combined, verified live against
    // the API on 2026-07-31. It was published as 128000 and VS 2026 agent mode believed it, so
    // every agent turn was rejected with context_length_exceeded. The cap is per-model, not
    // account-wide: gpt-oss-120b on the same key answered a 10084-token request.
    [InlineData("zai-glm-4.7",  8_192,   2_048)]
    [InlineData("gpt-oss-120b", 131_072, 65_536)]
    public void AllModels_HaveCorrectContextWindowConfig(
        string model, int expectedContextLength, int minMaxOutput)
    {
        ModelSelectionStore store  = new();
        ModelExecutionConfig exec  = store.GetExecutionConfigForModel(model, new Dictionary<string, ProviderInfo>());

        Assert.True(exec.ContextLength.HasValue,
            $"{model}: context_length must be configured");
        Assert.Equal(expectedContextLength, exec.ContextLength!.Value);

        if (minMaxOutput > 0)
        {
            Assert.True(exec.MaxOutputTokens.HasValue,
                $"{model}: max_output_tokens must be configured");
            Assert.True(exec.MaxOutputTokens!.Value >= minMaxOutput,
                $"{model}: max_output_tokens {exec.MaxOutputTokens} < expected {minMaxOutput}");
        }
    }

    // ─── reasoning_effort validity ──────────────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4-flash")]
    public void DeepSeek_ReasoningEffort_IsValidValue(string model)
    {
        ModelSelectionStore store  = new();
        ModelExecutionConfig exec  = store.GetExecutionConfigForModel(model, new Dictionary<string, ProviderInfo>());

        Assert.False(string.IsNullOrWhiteSpace(exec.ReasoningEffort),
            $"{model}: reasoning_effort should be configured in deepseek.json");

        string[] valid = ["low", "medium", "high", "default"];
        Assert.Contains(exec.ReasoningEffort, valid);
    }

    // ─── Temperature sanity bounds ──────────────────────────────────────

    [Theory]
    [InlineData("deepseek-v4-pro",   "deepseek")]
    [InlineData("deepseek-v4-flash", "deepseek")]
    [InlineData("qwen/qwen3-coder-480b-a35b-instruct", "nvidia")]
    [InlineData("moonshotai/kimi-k2.6", "nvidia")]
    [InlineData("nvidia/nemotron-3-super-120b-a12b", "nvidia")]
    [InlineData("openai/gpt-oss-120b",           "nvidia")]
    [InlineData("qwen/qwen3.5-397b-a17b",        "nvidia")]
    [InlineData("gpt-5.5-pro",   "openai")]
    [InlineData("gpt-5.5",       "openai")]
    [InlineData("gpt-5.4",       "openai")]
    [InlineData("gpt-5.4-mini",  "openai")]
    [InlineData("o4-mini",       "openai")]
    [InlineData("qwen/qwen3.6-27b",              "groq")]
    [InlineData("openai/gpt-oss-120b",           "groq")]
    [InlineData("meta-llama/llama-4-scout-17b-16e-instruct", "groq")]
    [InlineData("llama-3.3-70b-versatile",        "groq")]
    [InlineData("qwen/qwen3-32b",                "groq")]
    [InlineData("openai/gpt-oss-20b",            "groq")]
    [InlineData("groq/compound",                 "groq")]
    [InlineData("qwen/qwen3-coder", "openrouter")]
    [InlineData("nvidia/nemotron-3-super-120b-a12b", "openrouter")]
    [InlineData("moonshotai/kimi-k2.6",  "openrouter")]
    [InlineData("deepseek/deepseek-v4-pro", "openrouter")]
    [InlineData("kimi-k2.7-code", "moonshot")]
    [InlineData("kimi-k2.6",    "moonshot")]
    [InlineData("moonshot-v1-128k", "moonshot")]
    [InlineData("moonshot-v1-32k",  "moonshot")]
    [InlineData("zai-glm-4.7", "cerebras")]
    [InlineData("gpt-oss-120b", "cerebras")]
    [InlineData("qwen3-coder:480b", "ollamacloud")]
    [InlineData("devstral-2:123b",  "ollamacloud")]
    public void ConfiguredTemperature_IsWithinValidRange(string model, string provider)
    {
        _ = provider;
        ModelSelectionStore  store = new();
        ModelExecutionConfig exec  = store.GetExecutionConfigForModel(model, new Dictionary<string, ProviderInfo>());

        if (!exec.Temperature.HasValue) return;

        double t = exec.Temperature.Value;
        Assert.True(t is >= 0.0 and <= 2.0,
            $"{model}: temperature {t} is outside [0, 2.0]");
    }

    // ─── File existence ─────────────────────────────────────────────────

    [Fact]
    public void AllProviderConfigFiles_HaveAtLeastOneModel()
    {
        ModelSelectionStore store = new();
        var providers = store.ProviderModelSelections;

        Assert.True(providers.Count >= 7,
            $"Expected at least 7 provider configs (deepseek, openai, nvidia, groq, openrouter, moonshot, zenmux), got {providers.Count}");

        string[] expected = ["deepseek", "openai", "nvidia", "groq", "openrouter", "moonshot", "zenmux"];
        foreach (string name in expected)
        {
            Assert.True(providers.ContainsKey(name),
                $"Missing provider config: {name}");
            Assert.NotEmpty(providers[name]);
        }
    }

    // ─── Enabled model count per provider ───────────────────────────────

    [Theory]
    [InlineData("deepseek", 3)]      // v4-pro + v4-flash + deepseek-flash (verified live 2026-09-10; coder-6.7b disabled)
    [InlineData("openai", 4)]        // gpt-5.5, gpt-5.4, gpt-5.4-mini, o4-mini (gpt-5.5-pro is Responses-API only)
    [InlineData("nvidia", 8)]
    [InlineData("groq", 8)]          // + qwen/qwen3.8-27b (verified live 2026-09-10)
    [InlineData("openrouter", 10)]
    [InlineData("moonshot", 6)]      // 6 enabled (kimi-k2.5 disabled)
    [InlineData("cerebras", 2)]
    [InlineData("ollama", 11)]       // curated roster + gpt-oss:20b + nemotron-3-nano:30b (verified live 2026-09-10); single ollama.json
    [InlineData("zenmux", 0)]        // whole roster disabled 2026-07-31: HTTP 402 reject_no_credit on every model
    public void EnabledModelCount_IsCorrect(string providerName, int expectedEnabled)
    {
        ModelSelectionStore store = new();
        var entries = store.GetProviderModelSelections(providerName);
        int enabled = entries.Count(e => e.Enabled);
        Assert.Equal(expectedEnabled, enabled);
    }
}