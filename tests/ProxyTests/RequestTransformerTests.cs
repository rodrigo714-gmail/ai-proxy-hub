using System.Text.Json;

namespace ProxyTests;

public class RequestTransformerTests
{
    [Fact]
    public void ReplaceModelInRequestBody_ReplacesExistingModel()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"model":"old-model","messages":[]}""";

        string result = sut.ReplaceModelInRequestBody(raw, "new-model");

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal("new-model", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void ReplaceModelInRequestBody_AddsModelWhenMissing()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[]}""";

        string result = sut.ReplaceModelInRequestBody(raw, "added-model");

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal("added-model", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void ModifyRequest_InjectsCachedReasoningContentForAssistantMessage()
    {
        RequestTransformer sut = CreateTransformer(out ReasoningCacheService cache);
        cache.Set("assistant:0", "cached reasoning");

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                { "role": "assistant", "content": "hola" }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.NotNull(result);
        using JsonDocument modified = JsonDocument.Parse(result!);
        JsonElement msg = modified.RootElement.GetProperty("messages")[0];
        Assert.Equal("cached reasoning", msg.GetProperty("reasoning_content").GetString());
    }

    [Fact]
    public void ModifyRequest_RemovesEmptyAssistantMessagesWithoutToolCalls()
    {
        RequestTransformer sut = CreateTransformer();

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                { "role": "user", "content": "hola" },
                { "role": "assistant", "content": "" },
                { "role": "assistant", "content": "ok" }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.NotNull(result);
        using JsonDocument modified = JsonDocument.Parse(result!);
        JsonElement messages = modified.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("ok", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public void ModifyRequest_KeepsEmptyAssistantWhenToolCallsPresent()
    {
        RequestTransformer sut = CreateTransformer();

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                {
                  "role": "assistant",
                  "content": "",
                  "tool_calls": [
                    {
                      "id": "call_1",
                      "type": "function",
                      "function": { "name": "ping", "arguments": "{}" }
                    }
                  ]
                }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.Null(result);
    }

    [Fact]
    public void ApplyExecutionDefaults_KeepsBodyWhenNoConfiguredDefaults()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[],"stream":false}""";

        string result = sut.ApplyExecutionDefaults(raw, "unknown-model-without-config");

        Assert.Equal(raw, result);
    }

    [Fact]
    public void ApplyExecutionDefaults_InjectsTemperatureTopPAndMaxTokens()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[]}""";

        string result = sut.ApplyExecutionDefaults(raw, "deepseek-v4-pro");

        using JsonDocument doc = JsonDocument.Parse(result);
        JsonElement root = doc.RootElement;
        Assert.True(root.TryGetProperty("temperature", out JsonElement temp));
        Assert.Equal(0.2, temp.GetDouble());
        Assert.True(root.TryGetProperty("max_tokens", out JsonElement maxTok));
        Assert.Equal(8192, maxTok.GetInt32());
    }

    [Fact]
    public void ApplyExecutionDefaults_InjectsReasoningEffortForDeepSeek()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[]}""";

        string result = sut.ApplyExecutionDefaults(raw, "deepseek-v4-pro", ProviderCapabilitiesRegistry.Get("deepseek"));

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal("medium", doc.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ApplyExecutionDefaults_SkipsTopPForNativeReasoner()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[]}""";

        // deepseek-v4-pro has reasoning_effort configured, so it's a native reasoner
        string result = sut.ApplyExecutionDefaults(raw, "deepseek-v4-pro", ProviderCapabilitiesRegistry.Get("deepseek"));

        // top_p should NOT be injected for native reasoners
        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.TryGetProperty("top_p", out _));
    }

    [Fact]
    public void ApplyExecutionDefaults_SkipsTopKForDeepSeekProvider()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[],"top_k":40}""";

        string result = sut.ApplyExecutionDefaults(raw, "deepseek-v4-pro", ProviderCapabilitiesRegistry.Get("deepseek"));

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.TryGetProperty("top_k", out _));
    }

    [Fact]
    public void ApplyExecutionDefaults_KeepsTopKForNvidiaProvider()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[],"top_k":40}""";

        string result = sut.ApplyExecutionDefaults(raw, "some-model", ProviderCapabilitiesRegistry.Get("nvidia"));

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("top_k", out JsonElement topK));
        Assert.Equal(40, topK.GetInt32());
    }

    [Fact]
    public void ApplyExecutionDefaults_DoesNotInjectReasoningEffortForUnsupportedProvider()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[]}""";

        string result = sut.ApplyExecutionDefaults(raw, "deepseek-v4-pro", ProviderCapabilitiesRegistry.Get("groq"));

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void ApplyExecutionDefaults_PreservesExistingProperties()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[],"temperature":0.9,"custom":"value"}""";

        string result = sut.ApplyExecutionDefaults(raw, "deepseek-v4-pro");

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal(0.9, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("value", doc.RootElement.GetProperty("custom").GetString());
    }

    [Fact]
    public void ApplyExecutionDefaults_InjectReasoningEffortForOpenAiProvider()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[]}""";

        string result = sut.ApplyExecutionDefaults(raw, "deepseek-v4-pro", ProviderCapabilitiesRegistry.Get("openai"));

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.Equal("medium", doc.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void ModifyRequest_ReturnsNullWhenNoMessagesProperty()
    {
        RequestTransformer sut = CreateTransformer();

        using JsonDocument request = JsonDocument.Parse("""{"model":"m"}""");

        string? result = sut.ModifyRequest(request);

        Assert.Null(result);
    }

    [Fact]
    public void ModifyRequest_ReturnsNullWhenNoAssistantMessages()
    {
        RequestTransformer sut = CreateTransformer();

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                { "role": "user", "content": "hello" },
                { "role": "system", "content": "sys" }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.Null(result);
    }

    [Fact]
    public void ModifyRequest_KeepsAssistantWithFunctionCallEvenIfContentEmpty()
    {
        RequestTransformer sut = CreateTransformer();

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                {
                  "role": "assistant",
                  "content": "",
                  "function_call": { "name": "test", "arguments": "{}" }
                }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.Null(result);
    }

    [Fact]
    public void ModifyRequest_DoesNotReinjectWhenReasoningContentAlreadyPresent()
    {
        RequestTransformer sut = CreateTransformer(out ReasoningCacheService cache);
        cache.Set("assistant:0", "cached reasoning");

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                { "role": "assistant", "content": "hola", "reasoning_content": "already present" }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.Null(result);
    }

    [Fact]
    public void ModifyRequest_InjectsReasoningForToolCallKey()
    {
        RequestTransformer sut = CreateTransformer(out ReasoningCacheService cache);
        cache.Set("toolcall:call_1", "tool reasoning");

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                {
                  "role": "assistant",
                  "content": "using tool",
                  "tool_calls": [
                    { "id": "call_1", "type": "function", "function": { "name": "test" } }
                  ]
                }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.NotNull(result);
        using JsonDocument modified = JsonDocument.Parse(result!);
        JsonElement msg = modified.RootElement.GetProperty("messages")[0];
        Assert.Equal("tool reasoning", msg.GetProperty("reasoning_content").GetString());
    }

    [Fact]
    public void ReplaceModelInRequestBody_InvalidJson_ReturnsOriginal()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = "not-json";

        string result = sut.ReplaceModelInRequestBody(raw, "model");

        Assert.Equal(raw, result);
    }

    [Fact]
    public void ApplyExecutionDefaults_InvalidJson_ReturnsOriginal()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = "not-json";

        string result = sut.ApplyExecutionDefaults(raw, "deepseek-v4-pro");

        Assert.Equal(raw, result);
    }

    [Fact]
    public void ModifyRequest_RemovesAssistantWithNullContent()
    {
        RequestTransformer sut = CreateTransformer();

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                { "role": "user", "content": "hello" },
                { "role": "assistant", "content": null }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.NotNull(result);
        using JsonDocument modified = JsonDocument.Parse(result!);
        JsonElement messages = modified.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
    }

    [Fact]
    public void ModifyRequest_RemovesAssistantWithWhitespaceContent()
    {
        RequestTransformer sut = CreateTransformer();

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                { "role": "user", "content": "hello" },
                { "role": "assistant", "content": "   " }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.NotNull(result);
        using JsonDocument modified = JsonDocument.Parse(result!);
        JsonElement messages = modified.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
    }

    [Fact]
    public void ModifyRequest_RemovesAssistantWithEmptyArrayContent()
    {
        RequestTransformer sut = CreateTransformer();

        using JsonDocument request = JsonDocument.Parse("""
            {
              "model": "m",
              "messages": [
                { "role": "assistant", "content": [] }
              ]
            }
            """);

        string? result = sut.ModifyRequest(request);

        Assert.NotNull(result);
        using JsonDocument modified = JsonDocument.Parse(result!);
        JsonElement messages = modified.RootElement.GetProperty("messages");
        Assert.Equal(0, messages.GetArrayLength());
    }

    [Fact]
    public void ApplyExecutionDefaults_DoesNotInjectReasoningEffortForUnknownProvider()
    {
        // With capabilities, unknown providers have all feature flags set to false.
        // No reasoning_effort is injected regardless of model name.
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[]}""";

        string result = sut.ApplyExecutionDefaults(raw, "deepseek-v4-pro", default);

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void ApplyExecutionDefaults_StripsToolsForModelsFlaggedSupportsToolsFalse()
    {
        // Models flagged supports_tools=false in their config reject a tools payload outright
        // (e.g. compound models answer HTTP 400 "`tool calling` is not supported"),
        // so the tools/tool_choice fields must never reach them.
        string tempDir = Path.Combine(Path.GetTempPath(), "test-model-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "test.json"), """
            {
              "provider": "groq",
              "models": [
                {
                  "match": "test-compound",
                  "priority": 1,
                  "enabled": true,
                  "execution": {
                    "supports_tools": false
                  }
                }
              ]
            }
            """);

            RequestTransformer sut = CreateTransformer(out _, tempDir);
            string raw = """{"messages":[],"tools":[{"type":"function","function":{"name":"f","parameters":{}}}],"tool_choice":"auto"}""";

            string result = sut.ApplyExecutionDefaults(raw, "test-compound", default);

            using JsonDocument doc = JsonDocument.Parse(result);
            Assert.False(doc.RootElement.TryGetProperty("tools", out _));
            Assert.False(doc.RootElement.TryGetProperty("tool_choice", out _));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApplyExecutionDefaults_KeepsToolsForModelsThatSupportThem()
    {
        RequestTransformer sut = CreateTransformer();
        string raw = """{"messages":[],"tools":[{"type":"function","function":{"name":"f","parameters":{}}}],"tool_choice":"auto"}""";

        string result = sut.ApplyExecutionDefaults(raw, "qwen/qwen3.8-27b", default);

        using JsonDocument doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("tools", out _));
        Assert.True(doc.RootElement.TryGetProperty("tool_choice", out _));
    }

    private static RequestTransformer CreateTransformer()
    {
        return CreateTransformer(out _);
    }

    private static RequestTransformer CreateTransformer(out ReasoningCacheService cache, string? configDirOverride = null)
    {
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", "unit-test-key");
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_BASE_URL", "http://127.0.0.1:12345");

        ProviderHttpClientFactory httpClientFactory = new();
        ProviderRegistry providerRegistry = new(httpClientFactory);
        ModelSelectionStore modelSelectionStore = new(configDirOverride);
        ModelCatalogService modelCatalog = new(providerRegistry, modelSelectionStore);
        cache = new ReasoningCacheService();

        return new RequestTransformer(modelCatalog, cache);
    }
}
