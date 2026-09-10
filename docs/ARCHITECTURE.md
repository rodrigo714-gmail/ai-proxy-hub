# System Architecture

Comprehensive architecture documentation describing the proxy design, components, and data flow for **14 AI providers** (see [Overview](#overview) for the list).

## Table of Contents

- [Overview](#overview)
- [Component Architecture](#component-architecture)
- [Data Flow](#data-flow)
- [Service Dependencies](#service-dependencies)
- [Configuration Management](#configuration-management)
- [Request Lifecycle](#request-lifecycle)
- [Model Resolution & 3-Level Hint Solver](#model-resolution--3-level-hint-solver)
- [Qualified Model Aliases (model@provider)](#qualified-model-aliases-modelprovider)
- [Unpinned Aliases (model@auto)](#unpinned-aliases-modelauto)
- [Diagnostic Response Headers](#diagnostic-response-headers)
- [Image Passthrough Support](#image-passthrough-support)
- [Force-Mode Parameter Override](#force-mode-parameter-override)
- [Failing Over](#failing-over)
- [Upstream Error Handling](#upstream-error-handling)
- [Performance Optimizations](#performance-optimizations)

---

## Overview

The proxy is a high-performance ASP.NET Core minimal API application that bridges GitHub Copilot, Cursor, Continue.dev, Visual Studio BYOM, and Ollama clients to **fourteen** AI providers:

- DeepSeek
- OpenAI
- Google Gemini
- NVIDIA NIM
- Groq
- OpenRouter
- Ollama Cloud
- Moonshot / Kimi
- Cerebras
- Z.AI
- ZenMux
- Mistral
- SiliconFlow
- Cloudflare Workers AI

### Design Principles

1. **Multi-Provider Agnostic** — One API surface, N backends
2. **Zero Allocation Streaming** — Pass-through SSE without buffering
3. **Configuration-Driven** — Model defaults, routing, and force-mode flags via JSON
4. **Testability** — All services are unit-testable with in-memory fixtures
5. **Production-Ready** — Connection pooling, HTTP/2, timeout handling
6. **Curated, Not Exhaustive** — Up to 15 enabled models per provider; chosen for coding in VS 2026 via GitHub Copilot

### Technology Stack

- **Runtime:** .NET 10
- **Web Framework:** ASP.NET Core Minimal APIs (`WebApplication.CreateSlimBuilder`)
- **Serialization:** System.Text.Json
- **HTTP Client:** `SocketsHttpHandler` with 256 connections/server + HTTP/2 multiplexing
- **Testing:** xUnit 2.9.3 + `Microsoft.AspNetCore.Mvc.Testing` — **599 tests** in 25 test files
- **Dependencies:** none. The `.csproj` has zero `PackageReference` entries; everything used ships in the shared framework.

---

## Component Architecture

### Application Startup (`Program.cs`)

```csharp
// 1. Create slim builder
WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);

// 2. Register core services — all are singletons
builder.Services.AddSingleton<ProviderHttpClientFactory>();
builder.Services.AddSingleton<ProviderHealthService>();   // cooldowns; must precede ProviderRegistry
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<ModelSelectionStore>();
builder.Services.AddSingleton<ModelCatalogService>();
builder.Services.AddSingleton<ReasoningCacheService>();
builder.Services.AddSingleton<RequestTransformer>();
builder.Services.AddSingleton<OllamaResponseBuilder>();
builder.Services.AddSingleton<UsageTracker>();
builder.Services.AddSingleton<ProxyLogger>();
builder.Services.AddSingleton<ChatStreamingService>();
builder.Services.AddSingleton<UsageRollupStore>();        // durable per-day usage
builder.Services.AddSingleton<FreeTierCatalogStore>();    // free allowances + ToS verdicts
builder.Services.AddSingleton<UsageTrackerService>();
builder.Services.AddSingleton<ProviderBillingService>();

// 3. Background hosted services
builder.Services.AddHostedService<ProviderBenchmarkService>();
builder.Services.AddHostedService<UsageSnapshotService>();  // 60s snapshot + rollup flush

// 4. Middleware
app.UseUpstreamErrorHandling();
app.UseOptionalProxyAuthentication(proxyApiKey);
app.UseStaticFiles();       // wwwroot: dashboard markup + vendored Chart.js

// 5. Map endpoints
app.MapOpenAiEndpoints();   // /v1/models, /v1/chat/completions
app.MapUsageEndpoints();    // /usage, /usage/summary, /usage/pricing, /usage/reset
app.MapDashboardEndpoints();// /api/usage, /api/billing, /dashboard
app.MapFreeTierEndpoints(); // /api/free-tier/summary
app.MapOllamaEndpoints();   // /api/version, /api/tags, /api/show, /api/chat
app.MapHealthEndpoints();   // /health, /api/resilience/cooldowns, /api/resilience/reset
```

> `ProviderHealthService` is registered **before** `ProviderRegistry` because the registry takes it
> as an optional constructor argument; the container picks the widest constructor it can satisfy.
> Its own constructor is `public` for the same reason — the DI container ignores internal ones.

### Core Components

#### 1. `ProviderHttpClientFactory`

Creates and caches HTTP clients for each provider with auth headers, base URL, and connection pooling (256 connections/server, HTTP/2).

#### 2. `ProviderRegistry`

**Discovery order** follows the iteration of `ProviderCapabilitiesRegistry` — that registry is the
single source of truth for every provider's base URL, env prefix, API paths and parameter support;
consult it instead of mirroring it here.

#### 3. `ModelSelectionStore`

Loads and parses model metadata from `config/model-selection/*.json` — one file per provider
(14 today; `ollama.json` and `ollamacloud.json` both declare provider `ollama` and are merged).
Files are enumerated in **ordinal** order so equal-length match collisions resolve identically on
NTFS and ext4 — see `CLAUDE.md` §Testing gotchas before changing this.

#### 4. `ModelCatalogService`

Maintains a live catalog of available models from all providers, fetched at startup. Resolves cross-provider collisions by `(priority asc, provider order asc)`.

#### 5. `ReasoningCacheService`

Caches DeepSeek `reasoning_content` for multi-turn conversations.

#### 6. `RequestTransformer`

Normalizes, filters, and injects request parameters per provider. Honours `override_client_params` force-mode.

**Parameter filtering matrix:**

> Representative subset only. The authoritative per-provider flags are
> `SupportsReasoningEffort` / `SupportsTopK` in `ProviderCapabilitiesRegistry`, plus the
> per-model `execution` block in `config/model-selection/*.json`. Update those, not this table.

| Provider | temperature | top_p | top_k | reasoning_effort | tools |
|----------|:-----------:|:-----:|:-----:|:-----------------:|:-----:|
| DeepSeek | ✅ | ⚠️ omitted | ❌ | ✅ | ✅ |
| OpenAI | ✅ | ⚠️ omitted | ❌ | ✅ | ✅ |
| NVIDIA NIM | ✅ | ✅ | ✅ | ❌ | ✅ |
| Groq | ✅ | ✅ | ✅ | ❌ | ❌ |
| OpenRouter | ✅ | ✅ | ✅ | ❌ | ✅ |
| Ollama Cloud | ✅ | ✅ | ✅ | ❌ | ✅ |
| Moonshot/Kimi | ✅ | ✅ | ❌ | ❌ | ✅ |
| Cerebras | ✅ | ✅ | ✅ | ❌ | ✅ |
| ZenMux | ✅ | ✅ | ❌ | ❌ | ✅ |

#### 7. `OllamaResponseBuilder`

Converts OpenAI JSON response → Ollama NDJSON format. Also handles image format conversion (OpenAI multi-part → Ollama `images` array).

#### 8. `ChatStreamingService`

Handles streaming responses with format conversion (SSE ↔ NDJSON) and zero-copy passthrough.

---

## Data Flow

### Request Flow: `POST /v1/chat/completions` (Streaming)

```
Client (GitHub Copilot)
    ├─> POST /v1/chat/completions
    │   { "model": "deepseek-v4-pro", "messages": [...], "stream": true }
    ▼
OpenAiEndpoints.cs
    ├─> ProviderRegistry.ResolveModel("deepseek-v4-pro")
    ├─> ProviderRegistry.ResolveCandidates("deepseek-v4-pro")
    ├─> RequestTransformer.ApplyExecutionDefaults(...)
    ├─> ChatStreamingService.StreamChatCompletionAsync(...)
    │   ├─> Forward to upstream API
    │   ├─> Receive SSE stream
    │   ├─> ReasoningCacheService caches thinking content
    │   └─> Stream to client (pass-through, + diagnostic headers)
    ▼
Client (Stream complete)
```

### Request Flow: `POST /api/chat` with provider-qualified alias

```
Client (Visual Studio BYOM)
    ├─> POST /api/chat
    │   { "model": "z-ai/glm-5.2@nvidia:latest", "messages": [...], "stream": false }
    ▼
OllamaEndpoints.cs
    ├─> ProviderRegistry.ResolveModel("z-ai/glm-5.2@nvidia:latest")
    │   → StripTagSuffix: "z-ai/glm-5.2@nvidia"
    │   → Contains '@' → exact match: "z-ai/glm-5.2@nvidia" → provider: "nvidia"
    ├─> RequestTransformer.ApplyExecutionDefaults(...)
    ├─> Convert Ollama → OpenAI format (including image conversion)
    ├─> Forward to https://integrate.api.nvidia.com/v1/chat/completions
    ├─> Receive OpenAI response
    ├─> OllamaResponseBuilder: Convert to Ollama NDJSON
    └─> Response + X-Proxy-* diagnostic headers
```

---

## Qualified Model Aliases (model@provider)

The `/api/tags` endpoint emits `model` fields in `model@provider:latest` format. This ensures that when a client sends this model name back, the proxy routes it to the **exact provider** rather than falling back to the default (DeepSeek).

**Resolution chain:**
1. Client sends: `{"model": "deepseek-v4-pro@ollama:latest"}`
2. `StripTagSuffix(":latest")` → `deepseek-v4-pro@ollama`
3. `_modelToProvider.ContainsKey("deepseek-v4-pro@ollama")` → ✅
4. Returns `deepseek-v4-pro@ollama`, which maps to the **ollama** provider

**For bare model names** (no `@provider`), the proxy returns the lowest-priority claimant provider. To pin a provider, use the qualified `model@provider` form.

---

## Unpinned Aliases (model@auto)

Because every id `/api/tags` publishes is pinned, a client that only picks from that list can
never fail over — the candidate walk resolves to one provider and an upstream 402 or 413 goes
straight to the IDE. `@auto` is the unpinned counterpart, published alongside the pinned entries
for every model **two or more** active providers serve:

```
name : "AUTO - gpt-oss-120b:latest"
model: "gpt-oss-120b@auto:latest"
```

**Resolution chain:**
1. Client sends `{"model": "gpt-oss-120b@auto:latest"}`
2. `StripTagSuffix(":latest")` → `gpt-oss-120b@auto`
3. `ResolveModel` sees the reserved `auto` hint and returns the alias unchanged
4. `ResolveCandidates` consults `_autoAliases` **before** the qualified branch — which would
   otherwise see the `@` and pin it — and returns the full candidate list
5. `ResolveRoutePlan` moves any cooling provider to the back

**Why a separate table rather than `_upstreamToProviders`:** the same model has a different
upstream id at each provider — `gpt-oss-120b` (Cerebras), `openai/gpt-oss-120b` (Groq, NVIDIA),
`gpt-oss:120b` (Ollama) — so there is no single upstream key those candidates could share. Each
entry carries its own `(provider, upstreamId)` pair.

`ModelCatalogService.AutoAliasKey` decides what counts as "the same model": drop the vendor
prefix, fold the Ollama size tag's colon to a dash. Nothing else is stripped, because
under-grouping merely omits an AUTO entry while over-grouping would route a request to a model
the user never picked.

Advertised limits are the **floor** across candidates (`Min` context and output, `All` for
tools/vision). Publishing one provider's 128k when the next caps at 8k is how a request sized
against the tag list gets rejected the moment it fails over.

---

## Diagnostic Response Headers

Both endpoints include response headers for debugging:

| Header | Endpoints | Description |
|--------|-----------|-------------|
| `X-Proxy-Requested-Model` | Both | Model name as sent by client |
| `X-Proxy-Resolved-Model` | Both | Internal resolved model id |
| `X-Proxy-Upstream-Model` | Both | Model sent to upstream API |
| `X-Proxy-Provider` | Both | Provider that handled the request |
| `X-Proxy-Candidate-Count` | Both | How many providers could have served this model |
| `X-Proxy-Candidate-Index` | Both | Zero-based position of the provider that answered; non-zero means it failed over |
| `X-Proxy-Attempts` | Both | How many candidates were actually tried |
| `X-Proxy-Primary-Provider` | `/v1/*` | Primary candidate provider |
| `X-Proxy-Primary-Upstream` | `/v1/*` | Primary upstream model |

`X-Proxy-Provider` and `X-Proxy-Upstream-Model` are written immediately before the response body, so they always name the provider that **served** the request rather than the one tried first.

---

## Image Passthrough Support

The proxy supports vision models that accept image inputs. Image conversion happens automatically:

**OpenAI multi-part format** (input from SDKs):
```json
{"role": "user", "content": [{"type": "text", "text": "..."}, {"type": "image_url", "image_url": {"url": "data:image/..."}}]}
```

**Ollama images format** (input from Ollama clients):
```json
{"role": "user", "content": "...", "images": ["data:image/..."]}
```

**Conversion logic in `BuildOllamaChatRequest` (OpenAiEndpoints.cs):**
- Detects `content` as array (multi-part format)
- Extracts `text` parts → concatenates into `content` string
- Extracts `image_url` parts → writes as `images` array (Ollama format)
- Converts bare base64 to `data:image/png;base64,` prefix when needed

---

## Force-Mode Parameter Override

The `override_client_params` field on `ModelExecutionConfig` is a boolean. When `true`, `RequestTransformer.ApplyExecutionDefaults()` overwrites client-supplied values for `temperature`, `top_p`, `max_tokens`, and `reasoning_effort` with the configured value.

Currently enabled for:
- Moonshot `kimi-k2.7-code`, `kimi-k2.7-code-highspeed`, `kimi-k2.6` (Kimi K2.x mandates `temperature=1.0`)
- Ollama Cloud `kimi-k2.7-code` (same rule)

---

## Failing Over

Every chat path — `/v1/chat/completions` and `/api/chat`, streaming and non-streaming — walks the
candidate list returned by `ProviderRegistry.ResolveRoutePlan()` until one provider answers.

**Why streaming can fail over.** `HttpCompletionOption.ResponseHeadersRead` returns the upstream
status before any body byte arrives, and assigning `Response.StatusCode`/headers does not commit
the response in ASP.NET Core — only a write or an explicit flush does. So the failover point is
the upstream success check. Once the first byte reaches the client the response is committed to
that provider and there is no going back; that case is recorded, not retried.

### Deciding whether to retry — `Infrastructure/UpstreamFailureClassifier.cs`

The HTTP status alone does not say what went wrong, so the body is inspected:

| Status | Kind | Retry elsewhere? |
|---|---|---|
| `429` with an explicit quota keyword (`daily limit`, `monthly quota`, `out of credits`, Cloudflare's `daily free allocation`, Google's `resource has been exhausted … reset after`) | `QuotaExhausted` | yes |
| `429` bare | `RateLimit` | yes |
| `402` | `QuotaExhausted` (credit) | yes |
| `401` / `403` | `Auth`, or `QuotaExhausted` when the body says so | yes |
| `404` / `410` | `ModelUnavailable` (scoped to that model only) | yes |
| `408` / `5xx` | `Transient` | yes |
| *no response* — connection refused, DNS/TLS failure, or nothing within `timeout_seconds` | `Unreachable` | yes |
| `400` / `413` / `422` | `BadRequest` | **no** |

Two rules earn their keep:

- **A bare 429 is never promoted to an exhausted quota.** Only an explicit keyword does it,
  because standing a healthy provider down until midnight over a one-minute blip is far worse
  than paying for one retry.
- **A 4xx carrying rate-limit wording is demoted back to `RateLimit`.** Groq answers an over-TPM
  request with `413 Payload Too Large` and `"code":"rate_limit_exceeded"`; read literally that is
  a malformed request and the router would give up with every other provider idle. A genuinely
  oversized body — no rate-limit wording — still fails fast.

### Standing a provider down — `Services/ProviderHealthService.cs`

| Failure | Scope | Cooldown |
|---|---|---|
| `QuotaExhausted` (daily) | provider | until next local midnight |
| `QuotaExhausted` (monthly) | provider | until the 1st of next month |
| `QuotaExhausted` (credit) | provider | 6 h, then re-probe — credits refill on a top-up, not a clock |
| `RateLimit` | provider | `5s · 2ⁿ`, capped at 5 min |
| `Auth` | provider | 15 min |
| `Transient` | provider | nothing until 3 consecutive failures, then 30 s escalating |
| `Unreachable` | provider | 2 min, escalating to 30 min — **on the first occurrence**, because a hung provider costs a full timeout every time it is tried |
| `ModelUnavailable` | **(provider, model)** | 30 min, escalating to 24 h |

An upstream `Retry-After` (or an `x-ratelimit-reset-*` header) always wins over anything computed
locally. A successful response **halves** the stored failure count and deletes the entry at zero,
so a provider that recovered early stops being penalised — recovery is not purely timer expiry.

State is in-memory on purpose: a restart is exactly when you want to re-probe, and a stale
cooldown outliving a corrected API key would be worse than re-learning it.

### Ordering: degrade, never exclude

`ResolveRoutePlan` calls the untouched, pure `ResolveCandidates` and passes the result through
`ProviderHealthService.Order()`, which puts healthy providers first (original order preserved) and
appends cooling ones by soonest expiry. It **never returns an empty list**: across fourteen free
tiers a bad hour can cool them all, and a last-ditch attempt beats an error the user cannot act on.

`ResolveCandidates` stays health-free deliberately — `ProviderBenchmarkService` depends on that
purity, since it specifically wants to probe cooling providers, which is how they are rediscovered
as healthy.

An explicit `model@provider` pin resolves to a single candidate, so it is neither reordered nor
failed over: answering an explicit choice from a different provider is worse than an honest error.
Clients that want the opposite send `model@auto` instead, which is the only id in `/api/tags` that
reaches this reordering at all — see [Unpinned Aliases](#unpinned-aliases-modelauto).

## Upstream Error Handling

`Infrastructure/UpstreamErrorMiddleware.cs` maps transport failures to responses a client can act
on, so an unreachable host is never an empty HTTP 500 that Visual Studio reports only as "the
model failed":

| Failure | Response |
|---|---|
| `HttpRequestException` (connection refused, DNS/TLS) | **502 `UPSTREAM_UNREACHABLE`** |
| Nothing back within `timeout_seconds` | **504 `UPSTREAM_TIMEOUT`** |

Both carry a JSON body naming the provider and model. An upstream 200 whose body is not a
parseable OpenAI completion also returns **502** with the upstream body attached, rather than
throwing. Note the interaction with failover: a transport failure is the `Unreachable` kind in
`UpstreamFailureClassifier`, so it fails over to the next candidate and cools the provider on the
**first** occurrence — this middleware only runs when every candidate has already been spent.

---

## Performance Optimizations

- **Connection pooling:** 256 per provider, HTTP/2 enabled
- **Zero-copy streaming:** SSE/NDJSON passthrough without buffering
- **Slim builder:** Minimal middleware overhead
- **JSON:** System.Text.Json source-generated (no reflection)
- **Model metadata:** Loaded once on startup, cached in RAM

---

## Related Documentation

- [API.md](API.md) — Endpoint specifications
- [CONFIGURATION.md](CONFIGURATION.md) — Configuration reference
- [TESTING.md](TESTING.md) — Test architecture and running tests
- [AGENTS.md](AGENTS.md) — Quick reference for AI assistants