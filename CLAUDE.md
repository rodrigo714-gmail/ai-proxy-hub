# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

The project is **AI Proxy Hub** (GitHub: `rodrigo714-gmail/ai-proxy-hub`); the assembly, csproj and
solution are all named `ai-proxy-hub`. Older names (`vs2026-copilot-deepseek-v4`,
`deepseek-copilot-proxy`) are retired — do not reintroduce them.

## Branching rule (hard constraint)

- **Never** touch `main`. It is the protected release branch.
- All work happens on `develop`. Feature branches branch off `develop` and merge back into `develop`.
- Conventional Commits are required for every merge into `develop`.

## Build & Test

```bash
# Build
dotnet build

# Run all tests (599 tests, xUnit + WebApplicationFactory, fully offline)
dotnet test

# Run specific test suite
dotnet test --filter "FullyQualifiedName~ParameterValidationTests"
dotnet test --filter "FullyQualifiedName~EndpointTests"
dotnet test --filter "FullyQualifiedName~ModelSelectionStoreTests"
dotnet test --filter "FullyQualifiedName~OverrideClientParamsTests"
dotnet test --filter "FullyQualifiedName~ProviderModelHintTests"

# Run single test by method name
dotnet test --filter TestMethodName=MySpecificTest

# Verbose output
dotnet test --verbosity detailed

# Run the proxy locally (port 11434 default)
dotnet run

# Smoke-test every provider end-to-end, exactly as VS 2026 BYOM calls it
./scripts/test-all-providers.ps1            # non-streaming
./scripts/test-all-providers.ps1 -Stream    # streaming (what VS 2026 actually uses)
```

**Port 11434 is the real Ollama daemon's port.** If Ollama is installed and running, the proxy
cannot bind and will fail to start. Stop Ollama or set `PROXY_PORT` to something else.

Tests live in `tests/ProxyTests/`. The project targets **.NET 10.0** and uses `WebApplication.CreateSlimBuilder()`.

## What This Is

A high-performance ASP.NET Core **minimal API proxy** that bridges GitHub Copilot, Cursor, Continue.dev, Visual Studio BYOM, and Ollama clients to multiple AI providers through two API surfaces:

| API Surface | URL Prefix | Used By |
|---|---|---|
| OpenAI-compatible | `/v1/*` | Copilot, Cursor, Continue.dev, OpenAI SDKs |
| Ollama-compatible | `/api/*` | VS 2026 BYOM, native Ollama clients |

**Supported providers (14):** DeepSeek, OpenAI, Google, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud,
Moonshot/Kimi, Cerebras, Z.AI, ZenMux, Mistral, SiliconFlow, Cloudflare Workers AI.

The last three are free-tier oriented and ship with every model `"enabled": false` until verified
live — see *Free-tier quotas* below.

**Primary use case:** GitHub Copilot inside Visual Studio 2026 producing code completions and code chat. All curated model configs are optimised for this workload.

## Architecture

### Service Registration (all Singletons)

Every service is registered as a **singleton** in `Program.cs`. The entire DI graph:

```
ProviderHttpClientFactory  →  Creates/caches per-provider HttpClient with auth headers
ProviderRegistry           →  Resolves model name → ordered list of provider candidates;
                              ResolveModel() does 3-level "provider/model" hint resolution
ModelSelectionStore        →  Loads/parses config/model-selection/*.json (incl. override_client_params)
ModelCatalogService        →  Fetches live model catalogs from all providers on startup;
                              resolves cross-provider collisions by (priority asc, provider order asc)
ReasoningCacheService      →  Caches DeepSeek reasoning_content for multi-turn conversations
RequestTransformer         →  Injects defaults + filters unsupported params per provider;
                              honours override_client_params=true force-mode
OllamaResponseBuilder      →  Converts OpenAI JSON response → Ollama NDJSON format
ChatStreamingService       →  Handles SSE streaming + on-the-fly format conversion
ProviderHealthService      →  Cooldowns after quota/rate-limit failures; reorders candidates
UsageRollupStore           →  Durable per-day, per-provider token rollup (data/usage-rollup.json)
FreeTierCatalogStore       →  Loads config/free-tier/catalog.json (allowances, ToS verdicts)
UsageTrackerService        →  Per-provider live stats (tokens, latency, RPM, rate-limit headers)
UsageTracker               →  Per provider:model stats behind /usage
ProviderBillingService     →  Live balance probes (DeepSeek, OpenAI, OpenRouter); 5-min cache
ProxyLogger                →  Structured console/file logging incl. [FAILOVER] markers
ProviderBenchmarkService   →  Background HostedService monitoring provider health
UsageSnapshotService       →  Background HostedService: 60s snapshots + rollup flush
ModelRosterSyncService     →  Background HostedService: watches discovery lists and renews
                              config/model-selection/*.json (add new upstream ids, retire
                              vanished ones). ROSTER_MODE=off|observe|sync. Also the sink
                              behind IModelRosterObserver, wired onto ModelCatalogService.
```

### Endpoint Structure

- `Endpoints/OpenAiEndpoints.cs` — Maps `/v1/models`, `/v1/chat/completions`
- `Endpoints/OllamaEndpoints.cs` — Maps `/api/version`, `/api/tags`, `/api/show`, `/api/chat`
- `Endpoints/HealthEndpoints.cs` — Maps `/health`, `/api/resilience/cooldowns`, `/api/resilience/reset`
- `Endpoints/DashboardEndpoints.cs` — Maps `/api/usage`, `/api/billing`, `/dashboard`
- `Endpoints/FreeTierEndpoints.cs` — Maps `/api/free-tier/summary`
- `Endpoints/UsageEndpoints.cs` — Maps `/usage`, `/usage/summary`, `/usage/pricing`, `/usage/reset`
- `Endpoints/RosterEndpoints.cs` — Maps `/api/roster/diff` (GET, proposes) and `/api/roster/sync` (POST, applies; `?apply=true` forces a write in observe mode)
- `Endpoints/ResponsesEndpoints.cs` — **dead code**: `MapResponsesEndpoints` is never called from `Program.cs`
- `Middleware/` — Empty (auth middleware lives in `Infrastructure/ProxyAuthenticationMiddleware.cs`)

### Request Lifecycle

1. **Request arrives** → endpoint handler parses model name
2. **Model validated** → `ModelCatalogService.AvailableModels` (populated at startup)
3. **Defaults injected** → `RequestTransformer.ApplyExecutionDefaults()` reads config from `ModelSelectionStore` and injects temperature, max_tokens, reasoning_effort, etc. for the requested model
4. **Provider resolved** → `ProviderRegistry.ResolveCandidates(model)` returns ordered list of providers to try
5. **Forward to upstream** → via `ChatStreamingService` (streaming) or direct HTTP (non-streaming)
6. **Response converted** → if Ollama endpoint, `OllamaResponseBuilder` maps OpenAI → Ollama format
7. **Failover** → every chat path retries the next candidate, streaming included. See *Failover and quota awareness*.

### Model Configuration

Model metadata lives in `config/model-selection/{provider}.json` (14 files: `deepseek`, `openai`, `google`, `nvidia`, `groq`, `openrouter`, `moonshot`, `cerebras`, `zai`, `ollama`, `zenmux`, `mistral`, `siliconflow`, `cloudflare`). The filename is cosmetic — the `"provider"` field inside is what binds the file to a provider, and **exactly one file may declare a given provider**. Each file maps model names to execution defaults:

```json
{
  "provider": "deepseek",
  "models": [
    {
      "match": "deepseek-v4-pro",
      "priority": 1,
      "enabled": true,
      "execution": {
        "context_length": 1048576,
        "max_output_tokens": 384000,
        "temperature": 0.2,
        "max_tokens": 8192,
        "reasoning_effort": "high",
        "timeout_seconds": 180
      }
    }
  ]
}
```

- **Adding a new model:** edit the JSON for its provider + restart (no hot reload)
- **Adding a new provider:** add one entry to `ProviderCapabilitiesRegistry` + create its JSON. Nothing else — discovery, routing and filtering all read from that registry.
- Models with `"enabled": false` are excluded from `/v1/models` and `/api/tags`
- **Matching is by substring, longest match first.** `"gpt-5.4"` is a substring of `"gpt-5.4-mini"`, so specificity — not priority — decides which entry a model gets. Priority only breaks ties between equally specific entries.
- `execution.override_client_params` (bool, default `false`) — force-mode: overwrite the client's `temperature` / `top_p` / `max_tokens` / `reasoning_effort` with the configured value (Moonshot Kimi K2.x mandates `temperature=1.0`)
- `execution.uses_max_completion_tokens` (bool, default `false`) — send the token budget as `max_completion_tokens` and never `max_tokens`. Required by OpenAI GPT-5.x and the o-series, which answer HTTP 400 otherwise.
- `execution.supports_temperature` (bool, default `true`) — when `false`, strip `temperature` and `top_p` entirely. Same OpenAI models.

### Curated model cap

Each provider exposes **up to ~10 enabled models**, curated for coding strength in GitHub Copilot
inside Visual Studio 2026. Rosters were verified against each provider's live `/v1/models` (or
`/api/tags`) on **2026-07-30** — every enabled entry answered a real request.

Retired or unentitled models are kept in the JSON with `"enabled": false` plus a `_comment`
explaining why, so nobody re-adds them.

**Verify before editing a roster.** `./scripts/test-all-providers.ps1` is the check: a model that
`/v1/models` lists can still return 404 (not entitled), 410 (end of life) or 402 (needs a paid
plan). NVIDIA's `moonshotai/kimi-k2.6` and Ollama Cloud's `kimi-k3` are both examples.

### Model naming across the two API surfaces

`/api/tags` publishes each model twice over:

- `name` — `"GROQ - gpt-oss-120b:latest"`, the human-readable label Visual Studio shows.
- `model` — `"openai/gpt-oss-120b@groq:latest"`, the id the client sends back.

`ProviderRegistry.ResolveModel` must therefore cope with `<upstream>@<provider>:latest`, with the
`"PROVIDER - model"` display form, and with the OpenAI-style `provider/model` form.

Ollama upstream ids legitimately contain a colon (`gpt-oss:120b`, `mistral-large-3:675b`), so the
`:latest` tag is stripped from the **end**, never from the first colon.

An `@provider` (or `PROVIDER - `) hint that the named provider cannot satisfy falls back to the
default model rather than resolving across providers — answering an explicit "OLLAMA - x" pick
from NVIDIA is worse than an honest fallback.

#### `@auto` — the unpinned alias

Every id above is pinned, so a client that only ever picks from `/api/tags` **can never fail
over**: an upstream 402 or 413 reaches the IDE as a hard error with thirteen healthy providers
idle. That is the whole failover machinery sitting unused on the one path that matters.

So `/api/tags` also publishes an unpinned entry per model that **two or more active providers**
serve:

- `name` — `"AUTO - gpt-oss-120b:latest"`
- `model` — `"gpt-oss-120b@auto:latest"`

`auto` is a reserved token (`ProviderRegistry.AutoProviderToken`), never a real provider name.
`ResolveCandidates` checks the alias table *before* the qualified branch, which would otherwise
see the `@` and pin it to one provider.

The grouping is not a plain name match, because the same model has a different id at each
provider — `gpt-oss-120b` (Cerebras), `openai/gpt-oss-120b` (Groq, NVIDIA), `gpt-oss:120b`
(Ollama). `ModelCatalogService.AutoAliasKey` drops the vendor prefix and folds the Ollama size
tag's colon to a dash, so the candidate list carries **a per-provider upstream id** rather than
one shared name. It deliberately strips nothing else: under-grouping merely omits an AUTO entry,
while over-grouping would route a request to a model nobody picked (`…-a12b` and `…-a12b:free`
stay separate).

The advertised `context_length` / `max_output_tokens` are the **floor** across candidates and
`supports_tools` their AND — a limit the client sizes against has to hold for whichever candidate
ends up serving, and advertising one provider's 128k when the next caps at 8k is exactly how a
request dies on failover.

A single-provider model gets no AUTO entry: it would behave identically to the pinned one and
only lengthen the dropdown. Pinned entries are unchanged — picking "GROQ - x" still means Groq
and only Groq.

For the OpenAI-style `provider/model` form, `ResolveModel` tries three strategies in order:

1. **Verbatim** — the full id exists in the registry (e.g. `openai/gpt-oss-120b` is a registered key).
2. **Strip prefix** — strip the provider prefix and look up the bare name (e.g. `groq/qwen3.6-27b` → `qwen3.6-27b`).
3. **Suffix match within hinted provider** — find any upstream id owned by the hinted provider whose suffix equals the bare name. Must NOT cross providers — a `groq/` hint never resolves to an NVIDIA-owned id.

The corresponding test files are `tests/ProxyTests/ProviderModelHintTests.cs` and
`tests/ProxyTests/RoutingDiagnosticTests.cs`.

### Streaming format conversion (critical for VS 2026)

The two surfaces speak different stream formats, and each converts on the fly:

| Endpoint | Emits | Converts from |
|---|---|---|
| `/api/chat` | Ollama NDJSON, one object per line, terminated by `"done": true` | upstream OpenAI SSE (`ChatStreamingService.StreamOllamaAndCache`) |
| `/v1/chat/completions` | OpenAI SSE, terminated by `data: [DONE]` | upstream Ollama NDJSON (`OpenAiEndpoints.HandleOllamaCloudChatCompletion`) |

`/api/chat` must **never** emit `data:` frames. An Ollama client silently discards them, so the
model appears to answer nothing at all — which is exactly how this broke for 10 of the 11
providers before. `EndpointTests.ApiChat_Streaming_LastLineHasDoneTrue` guards it.

Reasoning models (DeepSeek, Nemotron, GLM) can spend their whole budget in `reasoning_content`
and return empty `content`. Both directions fall back to the reasoning text so the client never
sees a blank reply. The field is `reasoning_content` on DeepSeek-style providers but plain
`reasoning` on Cerebras, Groq and OpenRouter — the fallback accepts both
(`ReasoningFallbackTests.cs`).

Tool calls cross the format boundary in both directions for Ollama-native upstreams:
Ollama `tool_calls` (arguments as JSON *object*, no id) are converted to OpenAI wire format
(generated `id`, `type=function`, arguments as JSON *string*, `finish_reason: "tool_calls"`),
and OpenAI-format agent history (assistant `tool_calls`, `tool_call_id`) is rewritten to
Ollama format on the way up. Without this, every Ollama Cloud model looked text-only to
VS 2026 agent mode.

### Parameter Filtering Rules (RequestTransformer)

`RequestTransformer.ApplyExecutionDefaults()` strips unsupported parameters per provider before forwarding, and injects defaults for missing fields:

- `top_k` → removed for DeepSeek, OpenAI, Moonshot/Kimi; kept for NVIDIA, Groq, OpenRouter
- `reasoning_effort` → only DeepSeek and OpenAI o-series; removed for NVIDIA, Groq, Moonshot/Kimi
- `top_p` → omitted when `reasoning_effort` is set (DeepSeek API rule: "don't combine sampling parameters with reasoning")
- `tools`/`tool_choice` → forwarded to every provider, **stripped per model** when the model's `execution.supports_tools` is `false`. Groq's `compound`/`compound-mini` are the live case: they run Groq's own server-side tools and answer HTTP 400 to a client tools payload.
- `function_call` → removed for all (deprecated)
- `override_client_params=true` → force-overwrite the client value with the configured one for `temperature`, `top_p`, `max_tokens`, `reasoning_effort`
- `uses_max_completion_tokens=true` → rewrite `max_tokens` to `max_completion_tokens` (OpenAI GPT-5.x / o-series)
- `supports_temperature=false` → drop `temperature` and `top_p` entirely (same models)

### Provider-specific quirks

**Moonshot Kimi K2.x** rejects any request with `temperature ≠ 1.0`. Handled with
`"override_client_params": true` in `moonshot.json`; `RequestTransformer` overwrites the client's
`temperature` (and `top_p`, `max_tokens`, `reasoning_effort` when configured) before forwarding.
`OverrideClientParamsTests.cs` exercises it end-to-end.

**OpenAI GPT-5.x / o-series** answers `400 Unsupported parameter: 'max_tokens'` and rejects an
explicit temperature. Handled with `uses_max_completion_tokens` + `supports_temperature: false`
in `openai.json`. `gpt-5.5-pro` is Responses-API only (`404 This is not a chat model`) and stays
disabled for Ollama/BYOM clients.

**Cerebras `zai-glm-4.7`** is capped at **8192 tokens for messages and completion combined**, not
the 128000 the roster used to claim. VS 2026 agent mode believed the published figure, sent 13k of
context and got `400 context_length_exceeded` on every turn. The cap is per-model rather than
account-wide — `gpt-oss-120b` on the same key answers a 10084-token request — so verify each entry
rather than assuming a tier-wide limit. Cerebras publishes no context metadata in `/v1/models`, and
probing it is awkward because a prompt large enough to exceed the context also exceeds the TPM
budget, which answers 429 first.

**Z.AI** puts its OpenAI-compatible API under `https://api.z.ai/api/paas/v4` with a *relative*
chat path (`chat/completions`, no `v1/` prefix). `ProviderHttpClientFactory` appends a trailing
slash to every base URL, without which `HttpClient` would resolve the relative path against the
last path segment and silently drop `/v4`.

### Failover and quota awareness

Every chat path resolves an **ordered candidate list** and walks it — `/v1/chat/completions`
(streaming and not) and `/api/chat` (streaming and not). Nothing is written to the client until an
upstream answers successfully, which is what makes retrying safe; once one byte reaches the client
there is no failover, ever.

`Infrastructure/UpstreamFailureClassifier.cs` decides what a failure *means*, because the status
code alone does not say:

| Status | Kind | Next candidate? |
|---|---|---|
| 429 + quota wording in the body | `QuotaExhausted` | yes |
| 429 bare | `RateLimit` | yes |
| 402 | `QuotaExhausted` (credit) | yes |
| 401 / 403 | `Auth` (or `QuotaExhausted` if the body says so) | yes |
| 404 / 410 | `ModelUnavailable` | yes |
| 408 / 5xx | `Transient` | yes |
| *no response at all* (connection refused, DNS/TLS failure, or nothing back within `timeout_seconds`) | `Unreachable` | yes |
| 400 / 413 / 422 | `BadRequest` | **no** |

Two traps the classifier exists to avoid:

- **A bare 429 is never treated as an exhausted quota.** Only an explicit keyword
  (`daily limit`, `monthly quota`, `out of credits`, Cloudflare's `daily free allocation`, …)
  promotes it, because standing a healthy provider down until midnight over a one-minute blip is
  far worse than retrying.
- **Groq reports an over-TPM request as HTTP 413**, with `rate_limit_exceeded` in the body. Read
  literally that is a malformed request and the router would give up with ten providers idle, so a
  4xx carrying rate-limit wording is reclassified as `RateLimit`. A genuinely oversized body still
  fails fast.
- **A named short window outranks incidental quota vocabulary.** Cerebras answers an over-TPM
  request with `{"message":"Tokens per minute limit exceeded","param":"quota","code":
  "token_quota_exceeded"}`. The word "quota" appears twice — in the JSON *keys*, not the prose —
  so the loose `quota.*exceed` pattern matched and stood Cerebras down until local midnight over
  a limit that clears in sixty seconds. A pattern that does not name its own window
  (`quota-exceeded`, `billing-cap`) is now vetoed when the body says "per minute"/"per second"/
  TPM/RPM. Patterns that *do* name it (`daily-limit`, `monthly-limit`, Cloudflare, Google) are
  never vetoed, and neither are credit balances — a spent balance is not a window that rolls
  over, so nearby throttle wording says nothing about it.

`Services/ProviderHealthService.cs` then stands the provider down for a length that matches the
failure: until local midnight for a daily quota, until the 1st for a monthly one, 6h for a spent
credit balance, exponential seconds for a rate limit, 15 min for bad credentials, and 2 min
escalating to 30 min for a provider that never answered. `Unreachable` is the one kind that cools
down on the **first** occurrence rather than after a burst — a hung provider costs a full
`timeout_seconds` every time it is tried, so the next request should route around it. An upstream
`Retry-After` always wins over anything computed locally. A success **halves** the failure count
and clears the entry at zero, so a provider that recovered early is not still being punished.

Ordering **degrades, it never excludes**: `ResolveRoutePlan` moves cooling providers to the back of
the list but never drops them, because with fourteen providers a bad hour can cool them all and a
last-ditch attempt beats a hard error. `ResolveCandidates` stays pure — `ProviderBenchmarkService`
depends on that, since it specifically wants to probe cooling providers.

Diagnostic headers: `X-Proxy-Provider` and `X-Proxy-Candidate-Index` name the provider that
actually **served**; `X-Proxy-Attempts` reports how many candidates were burned. All three are set
on success as well as failure — `X-Proxy-Attempts` used to be written only when every candidate
failed, which left it absent from the response that most needs it: the one that succeeded, but not
on the first try.

`FailoverTests.cs` covers this end to end against two scriptable stubs
(`FakeProviders/ScriptedProviderStub.cs`), including that a 400 burns exactly one candidate and a
`model@provider` pin never fails over.

### Free-tier quotas and persistent usage

`config/free-tier/catalog.json` records each provider's published free allowance, its cadence, its
RPM/RPD limits and a **ToS verdict** (`ok`/`caution`/`ambiguous`/`avoid`/`unknown`) — many free
tiers restrict proxy or relay use, and that cost belongs next to the quota, not buried. It is data,
not C#, so a figure can be re-verified without a rebuild.

Two accounting rules keep the headline honest:

- **Shared pools count once.** Providers serving several variants from one budget share a
  `pool_key`; summing the variants would multiply the same allowance several-fold.
- **Uncapped tiers are listed, never summed.** A permanently-free provider that publishes only a
  rate limit has real value that cannot be expressed as tokens/month; `RPM × 24/7` is a fantasy.

`Services/UsageRollupStore.cs` keeps a per-day, per-provider aggregate in
`${PROXY_DATA_DIR:-<base>/data}/usage-rollup.json` — atomic write, 400-day retention, flushed every
60 s and on shutdown — so a **monthly** budget still means something after a restart. An unwritable
directory degrades to memory-only with a warning; a corrupt file starts empty rather than failing
startup. Deliberately a JSON rollup and not SQLite: a few thousand rows with one writer does not
justify the project's first native dependency.

`GET /api/free-tier/summary` returns allowance, usage and any active cooldown in one payload, which
is what lets the dashboard render a coherent quota panel from a single fetch.

> **For the Copilot workload the binding limit is usually RPM/RPD, not the token pool.** Mistral is
> the clearest case: the largest free pool of any provider (~1B tokens/month) behind roughly 2
> requests/minute, which makes it a fallback for chat rather than a primary for completions.

### Dashboard

`GET /dashboard` serves `wwwroot/dashboard.html`, with Chart.js vendored at
`wwwroot/vendor/chart.umd.min.js` (it used to load from a CDN, so the page broke offline).
`app.UseStaticFiles()` serves both — static-file middleware ships in the shared framework, so this
adds no package reference.

When `PROXY_API_KEY` is set, a browser cannot attach a bearer token to a navigation, so
`/dashboard`, `/vendor/` and `/health` are exempt (disable with `PROXY_DASHBOARD_PUBLIC=false`).
**The data endpoints are not exempt** — `/api/usage`, `/api/billing`, `/api/free-tier` and
`/api/resilience` expose spend, quota and key metadata. The page reads the key from `?key=` or
`localStorage` and sends it as `X-Proxy-Key`. Prefix matching is path-boundary aware, so
`/dashboard-secret` still returns 401.

### Upstream error handling

`UpstreamErrorMiddleware` maps transport failures to responses a client can act on:
`HttpRequestException` → **502 UPSTREAM_UNREACHABLE**, timeout → **504 UPSTREAM_TIMEOUT**, both
with a JSON body naming the provider and model. Without it, an unreachable host or an expired
`timeout_seconds` surfaced as an empty HTTP 500 and Visual Studio just said the model failed.

An upstream 200 whose body isn't a parseable OpenAI completion returns **502** with the upstream
body attached, rather than throwing.

### Configuration Sources (priority order)

1. System environment variables
2. `.env` file (loaded by `Program.cs` if present)
3. `appsettings.json`
4. Hardcoded defaults (port 11434, model `deepseek-v4-pro`)

### Testing Architecture

Tests use `WebApplicationFactory<Program>` with an **in-process stub provider** (no real API calls). The stub simulates OpenAI-compatible endpoints on a random port. Key patterns:

- `ProxyFixture` provides `HttpClient` wired to the in-process proxy
- Tests that construct a `ProviderRegistry` or otherwise touch process env vars MUST be in `[Collection("Proxy")]` — `ProxyFixture` boots `Program.cs`, which loads the developer's real `.env` into the process, so anything running in parallel with it races
- Those tests must also use `ProviderEnvScope`, which clears every `PROVIDER_*` variable derived from `ProviderCapabilitiesRegistry` and restores them on dispose. Never hand-write the list: a forgotten provider picks up a real API key from `.env` and quietly changes collision resolution
- **599 tests** across 25 test files covering endpoints, parameter validation, model selection, transformers, auth, reasoning cache, Ollama response building, JSON defaults, HTTP client factory, provider registry, `override_client_params` semantics, `provider/model` hint resolution, `@auto` fan-out (`AutoAliasTests.cs`), roster renewal (`ModelRosterSyncTests.cs`), and Ollama NDJSON streaming

## Credential Separation

Per `.github/copilot-instructions.md`: **Never confuse Ollama Cloud API keys with local proxy API keys.** Cloud provider keys are managed via `.env` variables (`PROVIDER_OLLAMACLOUD_API_KEY`, `PROVIDER_DEEPSEEK_API_KEY`, `PROVIDER_MOONSHOT_API_KEY`, `PROVIDER_CEREBRAS_API_KEY`, etc.). The optional `PROXY_API_KEY` controls access to the proxy itself and is unrelated.

`.env` is in `.gitignore` and is **never** committed. Only `.env.example` is tracked.

## Key Files Reference

| File | Purpose |
|---|---|
| `Program.cs` | Entry point, DI registration, endpoint mapping, env-var discovery |
| `Services/ProviderRegistry.cs` | Model → provider resolution; tag/`@provider`/`provider/model` hint resolvers; `ResolveCandidates` for failover lists |
| `Services/RequestTransformer.cs` | Parameter filtering + default injection; `override_client_params` force-mode; `max_completion_tokens` rewrite |
| `Services/ModelCatalogService.cs` | Live model catalog from all providers; cross-provider collision resolution |
| `Services/ModelSelectionStore.cs` | JSON config loader; longest-match-first entry lookup; merges files declaring the same provider |
| `Services/ChatStreamingService.cs` | Streaming; OpenAI SSE → Ollama NDJSON conversion incl. tool-call reassembly |
| `Services/ProviderCapabilitiesRegistry.cs` | Single source of truth for provider base URLs, paths, env prefixes and parameter support |
| `Services/ProviderHttpClientFactory.cs` | HttpClient creation with auth headers and base-URL normalisation |
| `Models/ModelExecutionConfig.cs` | Per-model execution flags parsed from `execution` |
| `Models/ProviderInfo.cs` | record struct `(Name, ApiKey, BaseUrl, Client, Capabilities)` |
| `Infrastructure/ProxyAuthenticationMiddleware.cs` | Optional bearer token auth |
| `Infrastructure/UpstreamErrorMiddleware.cs` | Transport failures → 502/504 JSON instead of empty 500 |
| `Infrastructure/UpstreamFailureClassifier.cs` | Classifies upstream failures; splits rate-limit from exhausted quota |
| `Services/ProviderHealthService.cs` | Cooldowns, success-decay recovery, candidate reordering |
| `Services/UsageRollupStore.cs` | Durable per-day usage rollup (survives restarts) |
| `Services/ModelRosterSyncService.cs` | Roster renewal: diff + apply of config/model-selection against live discovery |
| `Services/FreeTierCatalogStore.cs` | Free-tier allowances, pool dedup, ToS verdicts |
| `config/model-selection/` | Per-provider model JSON configs (14 files) |
| `config/free-tier/catalog.json` | Free-tier allowances and ToS verdicts per provider |
| `wwwroot/dashboard.html` | Dashboard markup (served by `/dashboard`) |
| `scripts/test-all-providers.ps1` | Live end-to-end smoke test of every published model |

Further detail is available in `docs/ARCHITECTURE.md`, `docs/AGENTS.md`, `docs/API.md`, `docs/CONFIGURATION.md`, `docs/TESTING.md`, and `docs/DEPLOYMENT.md`.
