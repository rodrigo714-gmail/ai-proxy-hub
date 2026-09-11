using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using ProxyTests.FakeProviders;
using Xunit;

namespace ProxyTests;

/// <summary>
/// Boots the proxy with two scriptable upstream providers that both claim the same model, so
/// failover can actually be exercised.
///
/// `openai/gpt-oss-20b` is enabled in both `groq.json` and `nvidia.json`, which is what makes
/// `ResolveCandidates` return two candidates for it. Both stubs are OpenAI-format, so all four
/// chat paths (`/v1/chat/completions` and `/api/chat`, streaming and not) route through them.
/// (The 120b served this role until NVIDIA retired it with HTTP 410 on 2026-09-11; the roster
/// sync caught it, and the 20b - also dual-served - is the stable replacement anchor.)
///
/// This lives in its own collection rather than extending <see cref="ProxyFixture"/>: adding a
/// second provider there would change `X-Proxy-Candidate-Count`, `/v1/models` and cross-provider
/// collision resolution under the existing suite. It is safe because
/// `xunit.runner.json` sets `parallelizeTestCollections: false`, so no other collection is
/// mutating `PROVIDER_*` environment variables at the same time.
/// </summary>
public sealed class FailoverFixture : IDisposable
{
    internal const string SharedModel = "openai/gpt-oss-20b";

    private readonly ProviderEnvScope _envScope;
    private readonly WebApplicationFactory<Program> _factory;

    internal ScriptedProviderStub Groq { get; }
    internal ScriptedProviderStub Nvidia { get; }
    public HttpClient Client { get; }

    public FailoverFixture()
    {
        // Clear every provider variable first, so the developer's real .env cannot add a third
        // provider and change the candidate ordering these tests assert on.
        _envScope = new ProviderEnvScope();

        Groq = new ScriptedProviderStub("GROQ", [SharedModel]);
        Nvidia = new ScriptedProviderStub("NVIDIA", [SharedModel]);

        Environment.SetEnvironmentVariable("PROVIDER_GROQ_API_KEY", "scripted-groq-key");
        Environment.SetEnvironmentVariable("PROVIDER_GROQ_BASE_URL", Groq.Url);
        Environment.SetEnvironmentVariable("PROVIDER_NVIDIA_API_KEY", "scripted-nvidia-key");
        Environment.SetEnvironmentVariable("PROVIDER_NVIDIA_BASE_URL", Nvidia.Url);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ASPNETCORE_ENVIRONMENT", "Testing"));
        Client = _factory.CreateClient();
    }

    /// <summary>
    /// Clears both scripts and attempt counters, and clears any cooldowns the previous test
    /// left behind.
    ///
    /// The cooldown state is a process-wide singleton, so without this reset a 429 recorded by
    /// one test would reorder candidates in the next one and make the suite order-dependent.
    /// </summary>
    internal async Task ResetAsync()
    {
        Groq.Reset();
        Nvidia.Reset();
        using HttpResponseMessage reset = await Client.PostAsync("/api/resilience/reset", content: null);
        reset.EnsureSuccessStatusCode();
    }

    internal ScriptedProviderStub StubNamed(string providerName) =>
        providerName.Equals("groq", StringComparison.OrdinalIgnoreCase) ? Groq : Nvidia;

    internal ScriptedProviderStub OtherThan(string providerName) =>
        providerName.Equals("groq", StringComparison.OrdinalIgnoreCase) ? Nvidia : Groq;

    /// <summary>Makes a provider unreachable until the handle is disposed.</summary>
    internal IDisposable BreakProvider(string providerName) => StubNamed(providerName).Break();

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        Groq.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Nvidia.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _envScope.Dispose();
    }
}

[CollectionDefinition("Failover")]
public class FailoverCollection : ICollectionFixture<FailoverFixture> { }

[Collection("Failover")]
public class FailoverTests(FailoverFixture fixture)
{
    private const string QuotaBody =
        """{"error":{"message":"Rate limit reached: you have exhausted your daily limit. Resets at 00:00 UTC.","type":"tokens"}}""";

    private static StringContent OpenAiBody(bool stream) => new(
        $$"""{"model":"{{FailoverFixture.SharedModel}}","messages":[{"role":"user","content":"hi"}],"stream":{{(stream ? "true" : "false")}}}""",
        Encoding.UTF8, "application/json");

    private static StringContent OllamaBody(bool stream) => new(
        $$"""{"model":"{{FailoverFixture.SharedModel}}","messages":[{"role":"user","content":"hi"}],"stream":{{(stream ? "true" : "false")}}}""",
        Encoding.UTF8, "application/json");

    /// <summary>
    /// Sends one clean request to learn which provider is candidate 0. Priorities live in the
    /// config JSONs, so asserting a hardcoded winner would break whenever those are re-tuned.
    /// </summary>
    private async Task<string> DiscoverPrimaryProviderAsync()
    {
        await fixture.ResetAsync();
        HttpResponseMessage probe = await fixture.Client.PostAsync("/v1/chat/completions", OpenAiBody(stream: false));
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        return probe.Headers.GetValues("X-Proxy-Provider").Single();
    }

    // ── The headline case: two candidates, first is out of quota ─────────────

    [Fact]
    public async Task BothProviders_ClaimTheSharedModel()
    {
        await fixture.ResetAsync();
        HttpResponseMessage r = await fixture.Client.PostAsync("/v1/chat/completions", OpenAiBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("2", r.Headers.GetValues("X-Proxy-Candidate-Count").Single());
    }

    [Fact]
    public async Task NonStreaming_QuotaExhausted_FailsOverToSecondProvider()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(429, QuotaBody);

        HttpResponseMessage r = await fixture.Client.PostAsync("/v1/chat/completions", OpenAiBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotEqual(primary, r.Headers.GetValues("X-Proxy-Provider").Single());
        Assert.Equal(1, fixture.StubNamed(primary).ChatAttempts);
        Assert.Equal(1, fixture.OtherThan(primary).ChatAttempts);

        string body = await r.Content.ReadAsStringAsync();
        Assert.Contains("HELLO_FROM_", body);
    }

    /// <summary>
    /// <c>X-Proxy-Attempts</c> is documented as always present, but it used to be written only on
    /// the all-candidates-failed paths. That left it absent from the one response a reader most
    /// wants it on: the request that succeeded, just not on the first try.
    /// </summary>
    [Fact]
    public async Task AttemptsHeader_CountsBurnedCandidates_OnSuccessToo()
    {
        string primary = await DiscoverPrimaryProviderAsync();

        await fixture.ResetAsync();
        HttpResponseMessage straightThrough = await fixture.Client.PostAsync("/v1/chat/completions", OpenAiBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, straightThrough.StatusCode);
        Assert.Equal("1", straightThrough.Headers.GetValues("X-Proxy-Attempts").Single());

        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(429, QuotaBody);
        HttpResponseMessage failedOver = await fixture.Client.PostAsync("/v1/chat/completions", OpenAiBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, failedOver.StatusCode);
        Assert.Equal("2", failedOver.Headers.GetValues("X-Proxy-Attempts").Single());
        Assert.Equal("1", failedOver.Headers.GetValues("X-Proxy-Candidate-Index").Single());
    }

    /// <summary>
    /// The reason this whole feature exists: /api/chat is the Visual Studio 2026 BYOM path and
    /// used to resolve a single provider with no retry at all.
    /// </summary>
    [Fact]
    public async Task ApiChat_NonStreaming_FailsOverToSecondProvider()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(429, QuotaBody);

        HttpResponseMessage r = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotEqual(primary, r.Headers.GetValues("X-Proxy-Provider").Single());
        Assert.Equal(1, fixture.StubNamed(primary).ChatAttempts);
        Assert.Equal(1, fixture.OtherThan(primary).ChatAttempts);
    }

    [Fact]
    public async Task ApiChat_Streaming_FailsOverBeforeFirstByte()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(429, QuotaBody);

        HttpResponseMessage r = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: true));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotEqual(primary, r.Headers.GetValues("X-Proxy-Provider").Single());

        // Still Ollama NDJSON, still terminated by done:true — the format guarantee that
        // EndpointTests.ApiChat_Streaming_LastLineHasDoneTrue protects must survive failover.
        string body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("data:", body);
        string lastLine = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
        Assert.Contains("\"done\":true", lastLine.Replace(" ", ""));
    }

    [Fact]
    public async Task OpenAiStreaming_FailsOverBeforeFirstByte()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(503, """{"error":"upstream unavailable"}""");

        HttpResponseMessage r = await fixture.Client.PostAsync("/v1/chat/completions", OpenAiBody(stream: true));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotEqual(primary, r.Headers.GetValues("X-Proxy-Provider").Single());

        string body = await r.Content.ReadAsStringAsync();
        Assert.Contains("data:", body);
        Assert.Contains("[DONE]", body);
    }

    // ── The 400 rule: a malformed request must not burn every candidate ──────

    [Fact]
    public async Task BadRequest_BurnsExactlyOneCandidate()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(400, """{"error":{"message":"Unsupported parameter: 'max_tokens'","type":"invalid_request_error"}}""");

        HttpResponseMessage r = await fixture.Client.PostAsync("/v1/chat/completions", OpenAiBody(stream: false));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(1, fixture.StubNamed(primary).ChatAttempts);
        Assert.Equal(0, fixture.OtherThan(primary).ChatAttempts);

        // The client gets the provider's real explanation, not a synthetic proxy error.
        Assert.Contains("Unsupported parameter", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ApiChat_BadRequest_BurnsExactlyOneCandidate()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(400, """{"error":{"message":"Unsupported parameter: 'top_k'","type":"invalid_request_error"}}""");

        HttpResponseMessage r = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(1, fixture.StubNamed(primary).ChatAttempts);
        Assert.Equal(0, fixture.OtherThan(primary).ChatAttempts);
    }

    // ── All candidates fail: report the real upstream error ──────────────────

    [Fact]
    public async Task AllCandidatesFail_ReturnsTheLastRealStatusAndBody()
    {
        await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.Groq.Fail(429, QuotaBody);
        fixture.Nvidia.Fail(429, QuotaBody);

        HttpResponseMessage r = await fixture.Client.PostAsync("/v1/chat/completions", OpenAiBody(stream: false));

        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Equal(1, fixture.Groq.ChatAttempts);
        Assert.Equal(1, fixture.Nvidia.ChatAttempts);

        // Not the old misleading 502 {"error":"no provider candidate available"}.
        string body = await r.Content.ReadAsStringAsync();
        Assert.Contains("exhausted your daily limit", body);
        Assert.DoesNotContain("no provider candidate available", body);
    }

    [Fact]
    public async Task ApiChat_AllCandidatesFail_ReturnsTheLastRealStatusAndBody()
    {
        await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.Groq.Fail(500, """{"error":"boom"}""");
        fixture.Nvidia.Fail(500, """{"error":"boom"}""");

        HttpResponseMessage r = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal(1, fixture.Groq.ChatAttempts);
        Assert.Equal(1, fixture.Nvidia.ChatAttempts);
        Assert.Contains("boom", await r.Content.ReadAsStringAsync());
    }

    // ── An explicit pin must not silently answer from somewhere else ─────────

    [Fact]
    public async Task PinnedProvider_DoesNotFailOver()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(429, QuotaBody);

        // "model@provider" resolves to exactly one candidate by design: answering an explicit
        // pick from a different provider is worse than an honest error.
        var pinned = new StringContent(
            $$"""{"model":"{{FailoverFixture.SharedModel}}@{{primary.ToLowerInvariant()}}","messages":[{"role":"user","content":"hi"}],"stream":false}""",
            Encoding.UTF8, "application/json");

        HttpResponseMessage r = await fixture.Client.PostAsync("/v1/chat/completions", pinned);

        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.Equal("1", r.Headers.GetValues("X-Proxy-Candidate-Count").Single());
        Assert.Equal(1, fixture.StubNamed(primary).ChatAttempts);
        Assert.Equal(0, fixture.OtherThan(primary).ChatAttempts);
    }

    // ── Cooldown: the point of the whole exercise ────────────────────────────

    /// <summary>
    /// After a provider reports an exhausted daily quota, the very next request must not touch
    /// it again. Without a cooldown the router would keep paying a full round-trip to a provider
    /// it already knows is out until midnight, on every single request.
    /// </summary>
    [Fact]
    public async Task QuotaExhausted_Provider_IsSkippedOnTheNextRequest()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(429, QuotaBody);

        // First request: pays the failed attempt, then fails over.
        HttpResponseMessage first = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, fixture.StubNamed(primary).ChatAttempts);

        // Second request: the exhausted provider is now demoted, so it is not tried at all.
        HttpResponseMessage second = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(primary, second.Headers.GetValues("X-Proxy-Provider").Single());
        Assert.Equal(1, fixture.StubNamed(primary).ChatAttempts);   // still 1 — never retried
        Assert.Equal(2, fixture.OtherThan(primary).ChatAttempts);
    }

    [Fact]
    public async Task CooldownEndpoint_ReportsTheExhaustedProviderAndTheFailover()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(429, QuotaBody);

        await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));

        string json = await fixture.Client.GetStringAsync("/api/resilience/cooldowns");

        Assert.Contains(primary, json);
        Assert.Contains("QuotaExhausted", json);
        Assert.Contains("daily-limit", json);
        Assert.Contains("recent_failovers", json);
    }

    [Fact]
    public async Task ResetEndpoint_ReEnablesACooledDownProvider()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(429, QuotaBody);
        await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));

        Assert.NotEmpty(await ReadCooldownsAsync());

        using HttpResponseMessage reset = await fixture.Client.PostAsync($"/api/resilience/reset?provider={primary}", content: null);
        reset.EnsureSuccessStatusCode();

        // The cooldown is gone. The failover history is a log and deliberately survives.
        Assert.Empty(await ReadCooldownsAsync());
    }

    private async Task<List<System.Text.Json.JsonElement>> ReadCooldownsAsync()
    {
        string json = await fixture.Client.GetStringAsync("/api/resilience/cooldowns");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("cooldowns").EnumerateArray().Select(e => e.Clone())];
    }

    /// <summary>
    /// A single transient 503 must not stand a provider down — only a repeated pattern does.
    /// Otherwise one blip would demote a healthy provider for every subsequent request.
    /// </summary>
    [Fact]
    public async Task SingleTransientFailure_DoesNotDemoteTheProvider()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(503, """{"error":"blip"}""");

        await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));

        // Next request goes back to the primary, which is healthy again.
        HttpResponseMessage second = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(primary, second.Headers.GetValues("X-Proxy-Provider").Single());
    }

    /// <summary>
    /// `openai/gpt-oss-120b@groq` is a real id: Groq serves a model whose upstream name starts
    /// with `openai/`. The `provider/model` prefix used to pin it to OpenAI on the /v1 surface,
    /// which rejected it as an invalid model — the explicit `@provider` suffix must win.
    /// </summary>
    [Fact]
    public async Task AtProviderSuffix_BeatsALookalikeProviderPrefix()
    {
        await fixture.ResetAsync();

        var pinned = new StringContent(
            $$"""{"model":"{{FailoverFixture.SharedModel}}@groq","messages":[{"role":"user","content":"hi"}],"stream":false}""",
            Encoding.UTF8, "application/json");

        HttpResponseMessage r = await fixture.Client.PostAsync("/v1/chat/completions", pinned);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("groq", r.Headers.GetValues("X-Proxy-Provider").Single());
        Assert.Equal(1, fixture.Groq.ChatAttempts);
        Assert.Equal(0, fixture.Nvidia.ChatAttempts);
    }

    // ── Transport failures: unreachable or hung providers ────────────────────

    /// <summary>
    /// A provider that refuses the connection must not take the whole request down. This used to
    /// throw straight past the candidate loop into UpstreamErrorMiddleware, which answered 502
    /// with every other provider untried — the exact failure NVIDIA produced in practice when its
    /// free tier queued a model past its timeout.
    /// </summary>
    [Fact]
    public async Task UnreachableProvider_FailsOverToTheNextCandidate()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();

        using (fixture.BreakProvider(primary))
        {
            HttpResponseMessage r = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.NotEqual(primary, r.Headers.GetValues("X-Proxy-Provider").Single());
            Assert.Equal(1, fixture.OtherThan(primary).ChatAttempts);
        }
    }

    [Fact]
    public async Task UnreachableProvider_FailsOverOnTheOpenAiSurfaceToo()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();

        using (fixture.BreakProvider(primary))
        {
            HttpResponseMessage r = await fixture.Client.PostAsync("/v1/chat/completions", OpenAiBody(stream: false));

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.NotEqual(primary, r.Headers.GetValues("X-Proxy-Provider").Single());
        }
    }

    [Fact]
    public async Task UnreachableProvider_FailsOverWhenStreaming()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();

        using (fixture.BreakProvider(primary))
        {
            HttpResponseMessage r = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: true));

            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            string body = await r.Content.ReadAsStringAsync();
            Assert.DoesNotContain("data:", body);
            Assert.Contains("\"done\":true", body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Replace(" ", ""));
        }
    }

    [Fact]
    public async Task UnreachableProvider_IsStoodDownForTheNextRequest()
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();

        using (fixture.BreakProvider(primary))
        {
            await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));

            // A provider that hangs costs a full timeout every time it is tried, so unlike a
            // transient blip it is demoted on the first occurrence.
            List<System.Text.Json.JsonElement> cooldowns = await ReadCooldownsAsync();
            Assert.Contains(cooldowns, c =>
                c.GetProperty("provider").GetString() == primary &&
                c.GetProperty("kind").GetString() == "Unreachable");
        }
    }

    [Fact]
    public async Task EveryProviderUnreachable_ReportsAnActionableError()
    {
        await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();

        using (fixture.BreakProvider("groq"))
        using (fixture.BreakProvider("nvidia"))
        {
            HttpResponseMessage r = await fixture.Client.PostAsync("/api/chat", OllamaBody(stream: false));

            Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
            string body = await r.Content.ReadAsStringAsync();
            Assert.Contains("UPSTREAM_UNREACHABLE", body);
            Assert.Contains("\"provider\"", body);
        }
    }

    // ── Other retryable statuses ─────────────────────────────────────────────

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task RetryableStatuses_FailOver(int status)
    {
        string primary = await DiscoverPrimaryProviderAsync();
        await fixture.ResetAsync();
        fixture.StubNamed(primary).Fail(status, """{"error":"nope"}""");

        HttpResponseMessage r = await fixture.Client.PostAsync("/v1/chat/completions", OpenAiBody(stream: false));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotEqual(primary, r.Headers.GetValues("X-Proxy-Provider").Single());
    }
}
