# Testing Guide

Comprehensive testing documentation covering unit tests, integration tests, and validation procedures.

## Table of Contents

- [Test Overview](#test-overview)
- [Running Tests](#running-tests)
- [Test Suites](#test-suites)
  - [Endpoint Tests](#endpoint-tests)
  - [Parameter Validation Tests](#parameter-validation-tests)
  - [Unit Test Files](#unit-test-files)
  - [Model Selection Tests](#model-selection-tests)
  - [Request Transformer Tests](#request-transformer-tests)
  - [Override Client Params Tests](#override-client-params-tests)
  - [Provider Model Hint Tests](#provider-model-hint-tests)
- [Test Architecture](#test-architecture)
- [Adding New Tests](#adding-new-tests)
- [Performance Testing](#performance-testing)
- [Continuous Integration](#continuous-integration)

---

## Test Overview

The proxy includes a comprehensive test suite covering every component of the routing, transformation, and provider-resolution pipeline. All tests are in-process: no real provider API calls are made.

### Test Statistics

- **Total Tests:** 599
- **Status:** ✅ All passing (598 passed, 1 skipped)
- **Framework:** xUnit 2.9.3 + `Microsoft.AspNetCore.Mvc.Testing`
- **Coverage Areas:**
  - ✅ Endpoint routing (OpenAI `/v1/*` & Ollama `/api/*` formats)
  - ✅ Parameter validation and per-provider filtering
  - ✅ Model selection and JSON config parsing
  - ✅ Request transformation logic
  - ✅ Streaming and non-streaming responses
  - ✅ Error handling and fallback logic
  - ✅ Reasoning content caching
  - ✅ JSON serialization defaults
  - ✅ Provider registry and model resolution
  - ✅ Proxy authentication middleware
  - ✅ Ollama response building
  - ✅ Provider HTTP client factory
  - ✅ Model selection store
  - ✅ Reasoning cache service
  - ✅ **`override_client_params` force-mode semantics**
  - ✅ **3-level `provider/model` hint resolution**
  - ✅ **Upstream failure classification** — rate limit vs exhausted quota, `Retry-After` parsing
  - ✅ **Provider cooldowns** — midnight/month boundaries, backoff caps, success decay (fake clock, no sleeping)
  - ✅ **Failover across all four chat paths** — including that a 400 burns exactly one candidate
  - ✅ **Free-tier accounting** — pool dedup, uncapped excluded, signup credits separated
  - ✅ **Usage rollup persistence** — round-trip, corrupt file recovery, month boundaries
  - ✅ **Dashboard + auth carve-out** — page public, data endpoints protected

### Test Technologies

- **Framework:** xUnit
- **Test Mode:** In-process with stub provider server
- **Isolation:** Tests that mutate process env vars share the `Proxy` collection fixture
- **Stub Provider:** Mock OpenAI-compatible endpoint for isolation (no external API calls)

---

## Running Tests

### Via Visual Studio

1. **Open Test Explorer:** `Test > Test Explorer` (Ctrl+E, T)
2. **Run All Tests:** Click the "Run All Tests" button
3. **Run Specific Test:** Right-click test name > `Run`
4. **Run Suite:** Right-click the class name > `Run`

### Via Command Line

```bash
# Run all tests
dotnet test

# Run with quiet output
dotnet test --verbosity quiet

# Run specific test suite
dotnet test --filter "FullyQualifiedName~ParameterValidationTests"
dotnet test --filter "FullyQualifiedName~OverrideClientParamsTests"
dotnet test --filter "FullyQualifiedName~ProviderModelHintTests"
dotnet test --filter "FullyQualifiedName~ReasoningCacheServiceTests"

# Run with coverage report
dotnet test /p:CollectCoverage=true

# Run a single test by method name
dotnet test --filter TestMethodName=MySpecificTest
```

### Via PowerShell

```powershell
cd D:\repos\ai-proxy-hub

# Run all tests with real-time output
dotnet test --logger "console;verbosity=detailed"

# Run and capture to file
dotnet test > logs/test-results.txt 2>&1
```

---

## Test Suites

### Endpoint Tests

**File:** `tests/ProxyTests/EndpointTests.cs`

Tests the proxy's HTTP endpoints against stub provider responses. Uses `WebApplicationFactory` with an in-process provider stub to avoid real API calls. The fixture spins up Kestrel on `http://127.0.0.1:0` (random port), points `PROVIDER_DEEPSEEK_BASE_URL` at it, and exposes an `HttpClient` to the proxy under test.

#### Test Classes

**1. OpenAI-Compatible Endpoints**
- `GET /v1/models` — List available models (OpenAI format)
- `POST /v1/chat/completions` — Chat completion (non-streaming)
- `POST /v1/chat/completions?stream=true` — Chat completion (streaming)

**2. Ollama-Compatible Endpoints**
- `GET /api/version` — Proxy version
- `GET /api/tags` — List models in Ollama format
- `GET /api/show?model=...` — Model details (GET)
- `POST /api/show` — Model details (POST)
- `POST /api/chat` — Chat completion (Ollama NDJSON streaming)

**3. Health & Diagnostics**
- `GET /health` — Proxy health status
- `GET /api/version` — Build version

#### Key Validations

- ✅ Response format matches OpenAI / Ollama spec
- ✅ Status codes are correct (200, 400, 502, 503)
- ✅ JSON structure is valid
- ✅ Streaming responses use proper delimiters
- ✅ `/v1/models` only returns ids the routing layer can actually accept (bare or `upstream@provider` — never raw `provider/model`)

---

### Parameter Validation Tests

**File:** `tests/ProxyTests/ParameterValidationTests.cs`

Validates that `RequestTransformer.ApplyExecutionDefaults()` correctly injects default parameters for every model/provider combination defined in `config/model-selection/*.json`. Only enabled models are tested against.

#### Test Coverage

**DeepSeek Models:**
- ✅ `deepseek-v4-pro` — reasoning_effort injected, top_p omitted (native reasoner)
- ✅ `deepseek-v4-flash` — reasoning_effort injected, top_p omitted
- ✅ `deepseek-coder-6.7b-instruct` — disabled in config, not tested as a "preferred" model

**NVIDIA NIM Models (8):**
- ✅ nvidia/nemotron-3-super-120b-a12b, nvidia/nemotron-3-ultra-550b-a55b, z-ai/glm-5.2, deepseek-ai/deepseek-v4-pro, openai/gpt-oss-120b, minimaxai/minimax-m3, nvidia/llama-3.3-nemotron-super-49b-v1.5, meta/llama-3.3-70b-instruct

**OpenAI Models (4):**
- ✅ gpt-5.5, gpt-5.4, gpt-5.4-mini, o4-mini

**Groq Models (7):**
- ✅ qwen/qwen3.6-27b, openai/gpt-oss-120b, openai/gpt-oss-20b, llama-3.3-70b-versatile, llama-3.1-8b-instant, groq/compound, groq/compound-mini (compound models: `supports_tools=false` strips client tools)

**Moonshot/Kimi Models (6):**
- ✅ kimi-k2.7-code, kimi-k2.7-code-highspeed (force-mode: temperature=1.0, override_client_params=true)
- ✅ kimi-k2.6 (force-mode: temperature=1.0, override_client_params=true)
- ✅ moonshot-v1-128k, moonshot-v1-auto, moonshot-v1-32k

**OpenRouter Models (10):**
- ✅ anthropic/claude-sonnet-4.6, openai/gpt-5.4, google/gemini-3.5-flash, deepseek/deepseek-v4-pro, qwen/qwen3.7-plus, qwen/qwen3-coder, moonshotai/kimi-k2.7-code, moonshotai/kimi-k2.6, x-ai/grok-4.3, nvidia/nemotron-3-super-120b-a12b

**Cerebras Models (2):**
- ✅ zai-glm-4.7, gpt-oss-120b

**Ollama Cloud Models (9 enabled):**
- ✅ kimi-k2.7-code, glm-5.2, deepseek-v4-pro, minimax-m3, nemotron-3-ultra, nemotron-3-super, glm-5.1, deepseek-v4-flash, gpt-oss:120b

#### Expected Behavior

| Model | reasoning_effort | temperature | top_p | top_k | override_client_params | Result |
|-------|------------------|-------------|-------|-------|------------------------|--------|
| deepseek-v4-pro | ✅ injected | ✅ injected | ❌ omitted (reasoner) | ❌ filtered | false | Valid |
| o4-mini | ✅ injected | ❌ stripped (`supports_temperature=false`) | ❌ stripped | ❌ filtered | false | Valid |
| llama-3.3-70b (NVIDIA) | ❌ filtered | ✅ injected | ✅ injected | ✅ injected | false | Valid |
| kimi-k2.6 (moonshot) | n/a | ✅ **overrides client** (1.0) | ✅ injected | ❌ filtered | **true** | Forced |
| moonshot-v1-128k | n/a | ✅ injected (default) | ✅ injected | ❌ filtered | false | Preserves client |

#### Test Execution

```bash
dotnet test --filter ClassName=ParameterValidationTests --verbosity detailed
```

---

### Unit Test Files

The proxy ships with the following test files in `tests/ProxyTests/`:

| File | Tests | Purpose |
|------|------:|---------|
| `EndpointTests.cs` | ~20 | End-to-end HTTP behaviour with `WebApplicationFactory` + stub provider |
| `ParameterValidationTests.cs` | ~50 | Per-model parameter injection (temperature, top_p, max_tokens, reasoning_effort) |
| `RequestTransformerTests.cs` | ~27 | Filter / inject defaults, per-model `supports_tools=false` stripping, streaming SSE → NDJSON, assistant-message cleanup |
| **`OverrideClientParamsTests.cs`** | **10** | **`override_client_params=true` force-mode overrides client values; default mode preserves them; JSON parsing of true/false/absent** |
| **`ProviderModelHintTests.cs`** | **7** | **3-level `provider/model` hint resolution in `ProviderRegistry.ResolveModel` + `ResolveCandidates` for `model@provider`** |
| `ProviderRegistryTests.cs` | ~15 | Provider discovery, `ResolveProvider`, `ResolveCandidates`, mapping updates |
| `ModelCatalogServiceTests.cs` | ~25 | Cross-provider collisions, priority tie-breaks, JSON config integration |
| `ModelSelectionStoreTests.cs` | ~50 | `GetExecutionConfigForModel`, `IsPreferredModel`, priority resolution per provider |
| `ModelSelectionTests.cs` | ~15 | JSON parser invariants (string vs object, `match`/`model`/`id` keys, `enabled` flag) |
| `ReasoningCacheServiceTests.cs` | ~17 | Multi-turn reasoning content cache |
| `OllamaResponseBuilderTests.cs` | ~15 | Format conversion OpenAI ↔ Ollama |
| `ProviderHttpClientFactoryTests.cs` | ~8 | Per-provider `HttpClient` config (auth headers, base URL, fallbacks) |
| `ProxyAuthenticationMiddlewareTests.cs` | ~19 | `PROXY_API_KEY` bearer token + the dashboard carve-out (page public, data endpoints protected, no prefix leaks) |
| `JsonDefaultsTests.cs` | ~9 | `System.Text.Json` snake_case + null handling |
| `PricingCatalogTests.cs` | ~15 | Price lookup, provider fallbacks, and the guard that every registered provider has one |
| **`ProviderCapabilitiesRegistryTests.cs`** | **~35** | **Guard: every provider declares its fields, prefixes/URLs/display names are unique, paths are relative, exactly one config file declares it** |
| **`UpstreamFailureClassifierTests.cs`** | **~56** | **Real 429/413 bodies from Groq, Gemini, Cloudflare, OpenAI, Cerebras; a named short window (per-minute, TPM/RPM) outranks incidental "quota" wording; `Retry-After` in every documented form** |
| **`ProviderHealthServiceTests.cs`** | **23** | **Cooldown durations, DST/month boundaries, success decay, ordering never returns empty** |
| **`FailoverTests.cs`** | **26** | **Two scripted stubs: failover on all four chat paths, 400 burns one candidate, pins never fail over, cooldown skips a dead provider, `X-Proxy-Attempts` counts burned candidates on success too** |
| **`AutoAliasTests.cs`** | **11** | **`model@auto` fan-out: grouping folds one model's per-provider spellings without merging different models, every tag form resolves, candidates carry a per-provider upstream id, pins still yield one candidate, cooling providers go last, `/api/tags` AUTO entries advertise the floor of their candidates** |
| **`FreeTierCatalogTests.cs`** | **~13** | **Pool dedup, uncapped never summed, signup credits separated, malformed catalog yields no budget** |
| **`UsageRollupStoreTests.cs`** | **~9** | **Restart survival, atomic write, corrupt-file recovery, retention pruning** |
| **`DashboardEndpointTests.cs`** | **~6** | **Dashboard served from `wwwroot`, Chart.js local not CDN, quota panels present** |
| **`DotEnvLoaderTests.cs`** | **12** | **Precedence: a real environment variable beats `.env`; an empty value never masks one** |
| **`ReasoningFallbackTests.cs`** | **4** | **Reasoning-only answers reach the client as content whether the upstream names the field `reasoning_content` (DeepSeek-style) or `reasoning` (Cerebras/Groq/OpenRouter), on both `/api/chat` paths** |
| **`ModelRosterSyncTests.cs`** | **14** | **Roster renewal: pure `ComputeDiff` decision tables (add / re-enable / retire / pending / non-chat skip / empty-observation safety), miss counting via `Observe`, and two integration cycles against `FakeProviderHandler` + a throwaway config dir (sync writes the addition, observe proposes but leaves the file byte-identical)** |
| **Total** | **599** | |

---

### Model Selection Tests

**Files:** `tests/ProxyTests/ModelSelectionTests.cs`, `ModelSelectionStoreTests.cs`, `ModelCatalogServiceTests.cs`

Validates that `ProviderRegistry`, `ModelSelectionStore`, and `ModelCatalogService` correctly resolve model-to-provider candidates, apply JSON-driven execution defaults, and handle cross-provider collisions.

#### Test Scenarios

**1. JSON parsing invariants** (`ModelSelectionTests.cs`)
- String entries: `["model-a", "model-b"]` parse as plain matches
- Object entries with `match`/`model`/`id` keys: all three alias forms accepted
- `enabled: false` is respected
- `priority` orders entries (lower = higher priority)
- Malformed files are skipped without crashing the loader
- Provider name lookup is case-insensitive
- All `execution` sub-fields parse (context_length, max_output_tokens, supports_tools, supports_vision, family, temperature, top_p, max_tokens, reasoning_effort, timeout_seconds, **override_client_params**)

**2. Curated-provider invariants** (`ModelSelectionStoreTests.cs`)
- Each provider exposes the expected number of enabled models (5 max, except DeepSeek/Cerebras with 2 and Ollama with 1)
- `deepseek-v4-pro` and `kimi-k2.6` lookups return valid `ModelExecutionConfig`
- `gpt-oss-120b` is offered by NVIDIA, Groq, Cerebras, and Ollama Cloud
- `kimi-k2.6` is offered by NVIDIA, Moonshot, and Ollama Cloud with `override_client_params=true`

**3. Cross-provider collisions** (`ModelCatalogServiceTests.cs`)
- `kimi-k2.6` is offered by Moonshot and Ollama Cloud → Moonshot wins (priority 1)
- `gpt-oss-120b` is offered by NVIDIA and Groq at the same priority → NVIDIA wins by discovery order
- `ResolveCandidates` returns ordered failover list

---

### Request Transformer Tests

**File:** `tests/ProxyTests/RequestTransformerTests.cs`

Tests the `RequestTransformer` class:
- Default injection (`temperature`, `top_p`, `max_tokens`, `reasoning_effort`) for missing fields
- Per-provider filtering (`top_k` removed for OpenAI/DeepSeek/Moonshot; `reasoning_effort` only for DeepSeek/OpenAI o-series)
- Native reasoner detection (skip `top_p` when `reasoning_effort` is set)
- `reasoning_content` re-injection for multi-turn conversations
- Empty assistant-message cleanup
- Streaming format conversion (SSE ↔ NDJSON)

The 25 tests in this file cover: `ReplaceModelInRequestBody`, `ModifyRequest` (assistant message handling + reasoning cache), and `ApplyExecutionDefaults` (all the parameter injection rules above).

---

### Override Client Params Tests

**File:** `tests/ProxyTests/OverrideClientParamsTests.cs` (new)

10 tests covering the `override_client_params` flag on `ModelExecutionConfig`. When a model has this flag set to `true`, the proxy **overwrites** client-supplied `temperature` / `top_p` / `max_tokens` / `reasoning_effort` with the configured value (instead of only injecting defaults for missing fields). The main use case is **Moonshot Kimi K2.x which mandates `temperature=1.0`** — the proxy must force that value even if the user supplied `0.7`.

**`ApplyExecutionDefaults` behavior (6 tests):**
- `OverrideClientParamsTrue_OverwritesClientTemperature` — kimi-k2.6 + moonshot: 0.7 → 1.0
- `OverrideClientParamsTrue_OverwritesClientMaxTokens` — kimi-k2.6 + moonshot: 32000 → 4096
- `OverrideClientParamsTrue_OverwritesClientTopP` — kimi-k2.6 + moonshot: 0.5 → 0.95
- `OverrideClientParamsFalse_KeepsClientValue` — moonshot-v1-128k preserves client's 0.7 and 1234
- `OverrideClientParamsFalse_InjectsMissingDefaults` — moonshot-v1-128k injects 0.3 / 4096 when client omits them
- `OverrideClientParamsTrue_AppliesToAllForcedFields` — all three numeric fields overwritten in one body

**`ModelExecutionConfig` parsing (4 tests):**
- `OverrideClientParams_DefaultsToFalse` (record-struct default)
- `OverrideClientParams_TrueIsParsed` from JSON `"override_client_params": true`
- `OverrideClientParams_FalseIsParsed` from JSON `"override_client_params": false`
- `OverrideClientParams_AbsentIsParsedAsFalse` (omitted field → false, not null)

---

### Provider Model Hint Tests

**File:** `tests/ProxyTests/ProviderModelHintTests.cs` (new)

7 tests covering the 3-level `provider/model` hint resolution in `ProviderRegistry.ResolveModel`. The OpenAI-style form `"nvidia/qwen3-coder-480b-a35b-instruct"` is accepted, but NVIDIA exposes many models with upstream ids that include a slash prefix (e.g. `qwen/qwen3.5-397b-a17b`), so the resolver tries three strategies in order.

**Level 1 — Verbatim match (1 test):**
- `nvidia/openai/gpt-oss-120b` → `openai/gpt-oss-120b` (registered under that exact key)

**Level 2 — Strip prefix, look up bare (2 tests):**
- `groq/qwen3-32b` → `qwen3-32b` (the bare name is registered)
- `groq/llama-3.3-70b-versatile` → `llama-3.3-70b-versatile`

**Level 3 — Suffix match within hinted provider (2 tests):**
- `nvidia/qwen3.5-397b-a17b` → `qwen/qwen3.5-397b-a17b` (the actual NVIDIA upstream id)
- `groq/qwen3.5-397b-a17b` → falls back to default (no groq entry has that suffix — must NOT cross providers)

**`ResolveCandidates` (2 tests):**
- `ResolveCandidates("openai/gpt-oss-120b@ollama")` — no ollama claimant → single fallback
- `ResolveCandidates("kimi-k2.6@moonshot")` — exact moonshot candidate, no failover

---

## Test Architecture

### Fixture-Based Isolation

The shared `ProxyFixture` collection:

1. **Spawns an in-process stub provider** listening on a random localhost port
   - Simulates OpenAI API endpoints (`/v1/models`, `/v1/chat/completions`, streaming SSE)
   - Returns fake but valid responses
   - No external network calls

2. **Points environment variables at the stub**
   - `PROVIDER_DEEPSEEK_BASE_URL` → local stub
   - Other providers → real URLs (or cleared in test env so only deepseek is discovered)

3. **Creates a `WebApplicationFactory<Program>` for the proxy**
   - Real ASP.NET Core middleware pipeline
   - Full end-to-end testing with the actual DI graph

### Test Isolation Pattern

```csharp
[Collection("Proxy")]
public class MyTests
{
    [Fact]
    public async Task MyTest()
    {
        HttpResponseMessage r = await Client.GetAsync("/v1/models");
        // Test response...
    }
}
```

Tests in the same collection share one fixture. Tests that mutate process env vars (e.g. `ModelCatalogServiceTests`, `ModelSelectionStoreTests`, `ProviderRegistryTests`) MUST be in the `Proxy` collection so they don't race with the fixture's env-var setup — **and must use `ProviderEnvScope`**, never a hand-written list of variables. `ProviderEnvScope` derives its list from `ProviderCapabilitiesRegistry.KnownProviders`, so it picks up new providers automatically; a hand-rolled snapshot silently leaves the new ones set, and a real key from the developer's `.env` then changes cross-provider collision resolution underneath the assertions.

### The failover fixture

`FailoverTests` cannot use `ProxyFixture`: that boots a single stub that always answers 200, and `FakeProviderHandler` only serves model catalogs, never `/v1/chat/completions`. So there is a second fixture:

- **`FakeProviders/ScriptedProviderStub.cs`** — an in-process Kestrel stub on port 0 with a queue of scripted `(status, body, headers)` replies and a `ChatAttempts` counter, so a test can assert *how many* candidates were burned. An empty queue falls back to success.
- **`FailoverFixture`** boots two of them as `groq` and `nvidia`, which both declare `openai/gpt-oss-120b` in their config, so `ResolveCandidates` returns two candidates.

It lives in its own `[Collection("Failover")]` rather than extending `ProxyFixture`, because adding a second provider there would change `X-Proxy-Candidate-Count`, `/v1/models` and collision resolution across the whole existing suite. This is safe because `xunit.runner.json` sets `"parallelizeTestCollections": false` — collections run one at a time, so two fixtures that both boot `Program.cs` never race on `PROVIDER_*`.

Cooldown state is a process-wide singleton, so every failover test starts with `await fixture.ResetAsync()`, which clears the stub scripts **and** posts to `/api/resilience/reset`. Without it, a 429 recorded by one test would reorder candidates in the next and make the suite order-dependent.

### Time-dependent tests never sleep

`ProviderHealthService` and `UsageRollupStore` both take an injectable `Func<DateTimeOffset>`. That is what lets `ProviderHealthServiceTests` cross midnight, a month boundary and a DST change in milliseconds, and lets `UsageRollupStoreTests` assert that July usage does not leak into August.

`UsageRollupStoreTests` also writes to a fresh temp directory per test — a test that wrote the developer's real rollup would silently corrupt their quota figures.

### Env-Var Snapshot

Tests that need to manipulate env vars snapshot and restore them in `Dispose`:

```csharp
public class MyTests : IDisposable
{
    private readonly Dictionary<string, string?> _envSnapshot;

    public MyTests()
    {
        _envSnapshot = new() { ["PROVIDER_DEEPSEEK_API_KEY"] = Environment.GetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY") };
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", "test-key");
    }

    public void Dispose()
    {
        foreach (var kv in _envSnapshot) Environment.SetEnvironmentVariable(kv.Key, kv.Value);
    }
}
```

### Cleanup

`ProxyFixture` implements `IDisposable`:

```csharp
public void Dispose()
{
    Client.Dispose();
    _factory.Dispose();
    _stub.StopAsync().GetAwaiter().GetResult();
    _stub.DisposeAsync().GetAwaiter().GetResult();
}
```

---

## Adding New Tests

### Step 1: Determine Test Type

| Scenario | Test Type | File |
|----------|-----------|------|
| Testing HTTP endpoint behaviour | Endpoint Test | `EndpointTests.cs` |
| Testing parameter defaults | Parameter Validation | `ParameterValidationTests.cs` |
| Testing model resolution/selection | Model Selection | `ModelSelectionStoreTests.cs` / `ModelCatalogServiceTests.cs` |
| Testing `RequestTransformer` internals | Request Transformer | `RequestTransformerTests.cs` |
| Testing `override_client_params` semantics | Force-mode | `OverrideClientParamsTests.cs` |
| Testing `provider/model` hint resolution | Hint resolver | `ProviderModelHintTests.cs` |
| Testing authentication middleware | Unit Test | `ProxyAuthenticationMiddlewareTests.cs` |
| Testing reasoning cache | Unit Test | `ReasoningCacheServiceTests.cs` |
| Testing HTTP client factory | Unit Test | `ProviderHttpClientFactoryTests.cs` |
| Testing Ollama responses | Unit Test | `OllamaResponseBuilderTests.cs` |
| Testing JSON serialization | Unit Test | `JsonDefaultsTests.cs` |
| Testing provider registry | Unit Test | `ProviderRegistryTests.cs` |

### Step 2: Add Test Method

```csharp
[Fact]  // For single scenario
// or [Theory] for parameterized tests
public async Task DescriptiveTestName()
{
    // Arrange
    string raw = """{"model":"deepseek-v4-pro","messages":[]}""";

    // Act
    string result = sut.ApplyExecutionDefaults(raw, "deepseek-v4-pro", "deepseek");

    // Assert
    using JsonDocument doc = JsonDocument.Parse(result);
    Assert.True(doc.RootElement.TryGetProperty("reasoning_effort", out _));
}
```

### Step 3: Use Parameterized Tests

```csharp
[Theory]
[InlineData("deepseek-v4-pro", "deepseek")]
[InlineData("gpt-5", "openai")]
public void ResolvesCorrectProvider(string model, string expectedProvider)
{
    IReadOnlyList<(ProviderInfo, string)> cands = registry.ResolveCandidates(model);
    Assert.Equal(expectedProvider, cands[0].Item1.Name);
}
```

### Step 4: Run and Verify

```bash
dotnet test --filter MyTest
```

---

## Performance Testing

### Benchmarking Endpoints

Use `BenchmarkDotNet`:

```bash
dotnet add package BenchmarkDotNet --version 0.13.2
```

Create `ProxyBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Mvc.Testing;

[MemoryDiagnoser]
public class ProxyBenchmarks
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [GlobalSetup]
    public void Setup()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [Benchmark]
    public async Task GetModels()
    {
        HttpResponseMessage r = await _client.GetAsync("/v1/models");
        _ = await r.Content.ReadAsStringAsync();
    }

    [Benchmark]
    public async Task ChatCompletion_NonStreaming()
    {
        var body = new StringContent(
            """{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}]}""",
            System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage r = await _client.PostAsync("/v1/chat/completions", body);
        _ = await r.Content.ReadAsStringAsync();
    }
}
```

Run with:
```bash
dotnet run -c Release --runtimes net10.0
```

### Load Testing

Use `NBomber` for concurrent request testing:

```csharp
using NBomber.CSharp;
using NBomber.Http.CSharp;

HttpClient httpClient = new();
ScenarioProps scenario = Scenario.Create("load_test", async context =>
{
    Request request = Http.CreateRequest("GET", "http://localhost:11434/v1/models");
    return await Http.Send(httpClient, request);
})
.WithoutWarmUp()
.WithLoadSimulations(Simulation.KeepConstant(copies: 100, during: TimeSpan.FromSeconds(30)));

NBomberRunner.RegisterScenarios(scenario).Run();
```

---

## Continuous Integration

### GitHub Actions Workflow

Create `.github/workflows/test.yml`:

```yaml
name: Test Suite

on:
  push:
    branches: [develop, 'feature/**']
  pull_request:
    branches: [develop]

jobs:
  test:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        dotnet-version: ['10.0']

    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET 10
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0'

      - name: Restore dependencies
        run: dotnet restore

      - name: Build
        run: dotnet build --configuration Release --no-restore

      - name: Run Tests
        run: dotnet test --configuration Release --no-build --verbosity normal --logger "trx"

      - name: Upload Test Results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results-${{ matrix.dotnet-version }}
          path: '**/TestResults/**'
```

> Note: `main` is the protected release branch and is NOT triggered by this CI.

### Pre-commit Hook

```bash
#!/bin/bash
# .git/hooks/pre-commit
echo "Running tests..."
dotnet test --configuration Debug
if [ $? -ne 0 ]; then
  echo "Tests failed. Commit aborted."
  exit 1
fi
```

Make executable:
```bash
chmod +x .git/hooks/pre-commit
```

---

## Test Data and Fixtures

### Mock Provider Responses

The stub provider in `ProxyFixture` serves:

```json
// GET /v1/models
{
  "object": "list",
  "data": [
    {
      "id": "test-model",
      "object": "model",
      "created": 1700000000,
      "owned_by": "test"
    }
  ]
}

// POST /v1/chat/completions (non-streaming)
{
  "id": "test-id",
  "object": "chat.completion",
  "created": 1700000000,
  "model": "test-model",
  "choices": [{
    "index": 0,
    "message": { "role": "assistant", "content": "hi from stub" },
    "finish_reason": "stop"
  }],
  "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
}

// Streaming response (SSE)
data: {"id":"t","object":"chat.completion.chunk",...,"delta":{"content":"Hi"}}
data: {"id":"t","object":"chat.completion.chunk",...,"delta":{},"finish_reason":"stop"}
data: [DONE]
```

---

## Troubleshooting

### Test Timeout

```bash
dotnet test --logger "console;verbosity=detailed" --timeout 30000
```

Common causes:
- A test mutated process env vars and didn't restore them
- A new model was added to a JSON config but no test updated
- HTTP test forgot to dispose its `HttpClient`

### Test Flakiness

```bash
for i in {1..5}; do dotnet test --filter MyTest || break; done
```

Common causes:
- Two test classes mutating the same env var simultaneously (use `[Collection("Proxy")]`)
- Port collision (ProxyFixture uses port 0 to avoid this)
- Timing in streaming tests

### xUnit Warnings

`xUnit1025` (duplicate `InlineData`): two theory rows have the exact same `(arg1, arg2, …)` tuple. xUnit silently drops duplicates. To avoid, either remove the redundant row or change the second row's tuple values to make the assertion unique.

`xUnit2002` (`Assert.NotNull` on a value type): use `Assert.True(cfg.ContextLength.HasValue)` or a direct property assertion instead.

---

## Related Documentation

- [API.md](API.md) — Endpoint reference
- [CONFIGURATION.md](CONFIGURATION.md) — Test configuration options
- [ARCHITECTURE.md](ARCHITECTURE.md) — Internal component structure
- [AGENTS.md](AGENTS.md) — Quick reference for AI assistants
