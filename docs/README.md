# AI Proxy Hub

> The fastest way to run DeepSeek, OpenAI, Google, NVIDIA, Groq, OpenRouter, Moonshot/Kimi, Cerebras, Z.AI, ZenMux, Ollama Cloud, Mistral, SiliconFlow and Cloudflare Workers AI models in GitHub Copilot, VS BYOM, and Ollama clients — **curated for coding inside Visual Studio 2026**.

**As of July 2026** — Tested with Visual Studio 2026 Insider Edition · .NET 10 · 599 offline tests

A high-performance, ultra-low-overhead HTTP proxy that connects GitHub Copilot and Ollama clients to **14 AI providers**: DeepSeek, OpenAI, Google, NVIDIA NIM, Groq, OpenRouter, Moonshot/Kimi, Cerebras, Z.AI, ZenMux, Ollama Cloud, Mistral, SiliconFlow and Cloudflare Workers AI. Built with .NET 10 and ASP.NET Core minimal APIs for maximum throughput and minimal allocations.

When a provider throttles you or its free quota runs out, the request hops to the next provider that serves the same model — on every chat path, streaming included.

| 🏗️ | Details |
|---|---|
| **Providers** | DeepSeek, OpenAI, Google, NVIDIA NIM, Groq, OpenRouter, Ollama Cloud, Moonshot/Kimi, Cerebras, Z.AI, ZenMux, Mistral, SiliconFlow, Cloudflare Workers AI |
| **Models** | Auto-discovered from each provider; curated to **5-15 enabled per provider** for coding |
| **Default Port** | `11434` |
| **Framework** | .NET 10 |
| **Tests** | **599 tests**, all offline ✅ |
| **Deploy** | Docker / bare metal |

## Key Features

- **🧠 Reasoning Content Caching** — Automatically captures DeepSeek's `reasoning_content` and re-injects it on subsequent messages for true multi-turn reasoning
- **🌐 Multi-Provider Support (14 providers)** — Route requests to any provider based on model name
- **🔁 Quota-aware failover** — Every chat path walks an ordered candidate list. A 429 whose body names a daily or monthly limit stands that provider down until the budget really resets; a bare 429 only backs off for seconds. Cooling providers are demoted, never dropped.
- **🆓 Free-tier budget tracking** — `config/free-tier/catalog.json` records each provider's published allowance, RPM/RPD limits and terms-of-service verdict; `/api/free-tier/summary` reports allowance, spend and remaining budget. Usage persists in `data/usage-rollup.json` so a *monthly* quota survives a restart.
- **🔄 Dual API Compatibility**
  - **OpenAI-compatible** (`/v1/chat/completions`) — works with GitHub Copilot, Cursor, Continue.dev, any OpenAI SDK
  - **Ollama-compatible** (`/api/chat`, `/api/tags`, `/api/show`) — works with VS BYOM and Ollama clients
- **📊 Usage Dashboard & Billing** — Real-time SPA dashboard at `/dashboard` with Chart.js showing usage, cost, latency, billing balance, and LLM Arena performance data. REST APIs at `/api/usage` and `/api/billing`.
- **💰 Pricing Calculator & Cost Tracking** — Automatic cost estimation per request using token counts + provider pricing. Tracked in `UsageTrackerService` per model and per provider.
- **🛡️ Force-mode parameter override** — `override_client_params: true` in model JSON force-overwrites client values for models with hard requirements (e.g. Moonshot Kimi K2.x mandates `temperature=1.0`)
- **🎯 3-level `provider/model` hint resolution** — `nvidia/qwen3.5-397b-a17b` correctly resolves to NVIDIA's family-prefixed upstream id `qwen/qwen3.5-397b-a17b`
- **📋 Curated model roster** — Top coding-optimised models per provider, hand-picked for GitHub Copilot in VS 2026, with pricing data from `PricingCatalog`
- **📈 Usage Snapshots & Projections** — Periodic snapshots for weekly/monthly cost projections via `UsageSnapshotService`
- **🖼️ Vision & Image Support** — Multi-part image content is automatically converted between OpenAI and Ollama formats for vision-capable models (e.g. qwen3.7-plus, gemini-3.5-flash)
- **🔍 Diagnostic Response Headers** — Every response includes `X-Proxy-Requested-Model`, `X-Proxy-Resolved-Model`, and `X-Proxy-Provider` for debugging routing decisions
- **⚡ Ultra-Performance** — HTTP/2 connection pooling (256 connections/server), zero-copy streaming, minimal allocations
- **📦 Zero-Copy Streaming** — SSE pass-through without buffering
- **🔧 No External Dependencies** — Uses only built-in ASP.NET Core and System.Text.Json; the `.csproj` has zero `PackageReference` entries
- **🐳 Docker-Ready** — Multi-stage Dockerfile and docker-compose.yml included
- **🔐 Optional Authentication** — Set `PROXY_API_KEY` to require a Bearer token. The dashboard *page* stays reachable (a browser cannot send a bearer token on a navigation) but its data endpoints do not; set `PROXY_DASHBOARD_PUBLIC=false` to lock the page too.

## Quick Start

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or Docker
- API keys for providers you want to use

### 1. Configure

Copy `.env.example` to `.env` and set your API keys:

```bash
cp .env.example .env
# Edit .env → set PROVIDER_DEEPSEEK_API_KEY=sk-your-key
```

Key environment variables:
```
PROVIDER_DEEPSEEK_API_KEY=sk-...
PROVIDER_OPENAI_API_KEY=sk-proj-...
PROVIDER_NVIDIA_API_KEY=nvapi-...
PROVIDER_GROQ_API_KEY=gsk-...
PROVIDER_OPENROUTER_API_KEY=sk-or-...
PROVIDER_OLLAMACLOUD_API_KEY=...
PROVIDER_MOONSHOT_API_KEY=sk-...
PROVIDER_CEREBRAS_API_KEY=csk-...
PROVIDER_ZENMUX_API_KEY=your-zenmux-key-here

PROXY_PORT=11434                    # (optional)
DEFAULT_MODEL=deepseek-v4-pro       # (optional)
```

`.env` is git-ignored and never committed. Only `.env.example` is tracked.

### 2a. Run with Docker (Recommended)

```bash
docker compose up -d
```

### 2b. Run with .NET

```bash
dotnet run
```

You should see startup output listing the providers that have keys and the curated models they expose.

## API Reference

### OpenAI-Compatible Endpoints

```
GET  /v1/models                          # List models (bare + 'upstream@provider' aliases)
POST /v1/chat/completions                # Chat (streaming or non-streaming)
GET  /health                             # Health check + provider summary
```

### Ollama-Compatible Endpoints

```
GET  /api/version                        # Version info
GET  /api/tags                           # List models (Ollama format, qualified aliases)
GET  /api/show?model=...                 # Model details
POST /api/show                           # Model details
POST /api/chat                           # Chat (Ollama format; NDJSON streaming)
```

### Dashboard, Quota & Billing Endpoints

```
GET  /dashboard                         # Real-time dashboard (static page, Chart.js vendored locally)
GET  /api/usage                         # Usage stats for all providers (JSON)
GET  /api/usage/{provider}              # Usage stats for a single provider (JSON)
GET  /api/billing                       # Billing info for all providers (JSON)
GET  /api/billing/{provider}            # Billing info for a single provider (JSON)
GET  /api/free-tier/summary             # Free allowance, spend this month, remaining, per provider
GET  /api/resilience/cooldowns          # Providers currently standing down + recent failovers
POST /api/resilience/reset              # Re-enable a provider now (?provider=&model=)
```

**[→ Full API Documentation](API.md)**

### Diagnostic Response Headers

Every chat completion response includes diagnostic headers to verify routing:

| Header | Description | Example |
|--------|-------------|---------|
| `X-Proxy-Requested-Model` | The model name the client sent | `deepseek-v4-pro:latest` |
| `X-Proxy-Resolved-Model` | The resolved internal model id | `deepseek-v4-pro` |
| `X-Proxy-Upstream-Model` | The model id sent to the upstream API | `deepseek-v4-pro` |
| `X-Proxy-Provider` | The provider that handled the request | `deepseek`, `ollama`, `zenmux` |
| `X-Proxy-Candidate-Count` | How many providers could have served this model | `1`, `3` |
| `X-Proxy-Candidate-Index` | Zero-based position of the provider that answered — non-zero means it failed over | `0`, `1` |
| `X-Proxy-Attempts` | How many candidates were actually tried | `1`, `2` |
| `X-Proxy-Primary-Provider` | Primary provider candidate (OpenAI endpoint) | `nvidia` |
| `X-Proxy-Primary-Upstream` | Primary upstream model (OpenAI endpoint) | `z-ai/glm-5.2` |

`X-Proxy-Provider` always names the provider that **served** the response, not the one that was
tried first — so a value that differs from `X-Proxy-Primary-Provider` is a failover you can see.


## Configuration

### GitHub Copilot (VS Code)

In VS Code settings:

```json
{
  "github.copilot.advanced": {
    "debug.chatOverride": {
      "provider": "openai",
      "endpoint": "http://localhost:11434/v1/chat/completions",
      "model": "deepseek-v4-pro"
    }
  }
}
```

### VS 2026 BYOM (the proxy's primary use case)

Point the Ollama BYOM at:
```
http://localhost:11434/api/chat
```

Top picks for coding in VS 2026:
- `kimi-k2.7-code` (Ollama Cloud / Moonshot) — 🥇 Kimi 2.7 code-specialized, 262K context, force-mode
- `glm-5.2` (Ollama Cloud) — 🥈 GLM 5.2 latest, 1M context, strong reasoning
- `deepseek-v4-pro` (DeepSeek / Ollama Cloud) — DeepSeek V4 Pro, 1M context, reasoning
- `zai-glm-4.7` (Cerebras) — GLM 4.7 at Cerebras speed, 128K context

### Continue.dev / Cursor

```json
{
  "models": [{
    "title": "DeepSeek V4 Pro",
    "provider": "openai",
    "model": "deepseek-v4-pro",
    "apiBase": "http://localhost:11434/v1"
  }]
}
```

## Documentation

- **[API.md](API.md)** — Complete endpoint reference with examples
- **[CONFIGURATION.md](CONFIGURATION.md)** — Setup, providers, parameter mapping, context windows
- **[ARCHITECTURE.md](ARCHITECTURE.md)** — System design, components, request lifecycle
- **[TESTING.md](TESTING.md)** — Test architecture, running tests, adding new tests
- **[DEPLOYMENT.md](DEPLOYMENT.md)** — Docker, Kubernetes, monitoring, troubleshooting
- **[AGENTS.md](AGENTS.md)** — Quick reference for AI assistants (Copilot, Claude, etc.)

## Performance

- **Connection pooling:** 256 per provider with HTTP/2 multiplexing
- **Streaming:** Zero-copy pass-through (minimal memory overhead)
- **Model metadata:** Loaded once on startup, cached in RAM
- **Typical latency:** <10ms proxy overhead
- **Test coverage:** 599 tests covering endpoints, parameters, model selection, transformations, force-mode, hint resolution, reasoning fallback, pricing, billing, usage tracking, image support, failure classification, cooldowns, failover and roster renewal

## Testing

```bash
# Run all tests
dotnet test

# Run specific suite
dotnet test --filter ClassName=EndpointTests
dotnet test --filter ClassName=OverrideClientParamsTests
dotnet test --filter ClassName=ProviderModelHintTests

# Verbose output
dotnet test --verbosity detailed
```

**[→ Testing Guide](TESTING.md)**

## How Reasoning Caching Works

DeepSeek models return `reasoning_content` with their responses. The proxy:

1. Captures reasoning from each assistant response
2. Stores it in `ReasoningCacheService`
3. Re-injects cached reasoning into subsequent assistant messages in the same conversation
4. Enables coherent multi-turn reasoning without losing context

## How Force-Mode Override Works

Some models have hard requirements that contradict the user's request. The proxy handles this with the `override_client_params` flag in `config/model-selection/*.json`:

- **Default (`false` / absent):** the proxy only injects defaults for missing fields. Client-supplied values win.
- **Force mode (`true`):** the proxy **overwrites** client-supplied values for `temperature`, `top_p`, `max_tokens`, `reasoning_effort` with the configured value.

The canonical use case is Moonshot Kimi K2.7-code, K2.6, and K2.5 (including via ZenMux) which reject any `temperature ≠ 1.0`. With `override_client_params: true` and `temperature: 1.0`, the proxy silently corrects the client's value before forwarding. See `OverrideClientParamsTests.cs` for the test suite.

## Provider Support

Each provider exposes a curated set of enabled models, prioritised for coding.

| Provider | # enabled | Top picks | Notes |
|----------|----------:|-----------|-------|
| **DeepSeek** | 2 | deepseek-v4-pro, deepseek-v4-flash | 1M context, native reasoning |
| **OpenAI** | 4 | gpt-5.5, gpt-5.4, gpt-5.4-mini, o4-mini | GPT-5.x/o-series use `max_completion_tokens`, no explicit temperature |
| **Google** | 8 | gemini-3.5-flash, gemini-3.1/3-pro-preview, gemini-3-flash-preview, gemini-2.5-* | 🆓 free-tier daily quota; 429s recover at midnight PT |
| **NVIDIA NIM** | 8 | nemotron-3-super/ultra, glm-5.2, deepseek-v4-pro, gpt-oss-120b, minimax-m3, llama-3.3-* | Free tier queues some models |
| **Groq** | 7 | qwen3.6-27b, gpt-oss-120b/20b, llama-3.3-70b, llama-3.1-8b, compound(-mini) | Speed-optimised; compound = server-side tools only |
| **OpenRouter** | 10 | claude-sonnet-4.6, gpt-5.4, gemini-3.5-flash, kimi-k2.7-code/k2.6, grok-4.3, qwen3.7-plus, qwen3-coder | Multi-backend passthrough |
| **Moonshot/Kimi** | 6 | kimi-k2.7-code(-highspeed), kimi-k2.6, moonshot-v1-* | Kimi K2.x forces `temperature=1.0` |
| **Cerebras** | 2 | zai-glm-4.7, gpt-oss-120b | Small curated set |
| **Z.AI** | 5 | glm-5.2, glm-5.1, glm-4.7(-flash/-flashx) | glm-4.7-flash is 🆓; the rest need account balance |
| **Ollama Cloud** | 9 | kimi-k2.7-code, glm-5.2, deepseek-v4-pro/flash, minimax-m3, nemotron-3-*, glm-5.1, gpt-oss:120b | Tool calls fully converted for /v1 agent clients |
| **ZenMux** | 0 *(disabled 2026-07-31)* | — | Whole roster answered HTTP 402 `reject_no_credit`; top up at zenmux.ai and re-enable in config |
| **Mistral** | 0 *(ships disabled)* | mistral-large, devstral-medium, codestral | 🆓 largest free pool (~1B tok/mo) but only ~2 RPM — a fallback, not a primary |
| **SiliconFlow** | 0 *(ships disabled)* | Qwen3-8B, DeepSeek-V3, GLM-4-9B | 🆓 free models capped at 50 req/day without purchased credit |
| **Cloudflare Workers AI** | 0 *(ships disabled)* | llama-3.3-70b-fp8-fast, qwen2.5-coder-32b | 🆓 10k neurons/day, resets 00:00 UTC. **Requires `PROVIDER_CLOUDFLARE_BASE_URL`** (it embeds your account id) |

> The three newest providers ship with every model `"enabled": false`. A model listed by
> `/v1/models` is not proof you are entitled to it — it can still answer 404, 402 or 410. Run
> `./scripts/test-all-providers.ps1` and enable only what actually responds.

**[→ Configuration Guide](CONFIGURATION.md#context-window-specifications)**

## Architecture Overview

```
Clients (Copilot, VS BYOM, Ollama)
    ↓
Proxy (localhost:11434)
  ├─ Parameter filtering (RequestTransformer, with override_client_params force-mode)
  ├─ Model routing (ProviderRegistry; 3-level provider/model hint resolution)
  ├─ Route planning (ResolveRoutePlan → ProviderHealthService demotes cooling providers)
  ├─ Failover loop (walks candidates; nothing written until an upstream succeeds)
  ├─ Failure classification (UpstreamFailureClassifier: rate limit vs exhausted quota)
  ├─ Reasoning caching (ReasoningCacheService)
  ├─ Format conversion (OpenAI ↔ Ollama, including image multi-part conversion)
  ├─ Streaming handler (ChatStreamingService)
  ├─ Usage accounting (UsageTrackerService live + UsageRollupStore durable)
  └─ Diagnostic headers (X-Proxy-*)
    ↓
Upstream Providers
  ├─ DeepSeek API          ├─ Moonshot/Kimi API      ├─ Mistral API
  ├─ OpenAI API            ├─ Cerebras API           ├─ SiliconFlow API
  ├─ Google Gemini API     ├─ Z.AI API               └─ Cloudflare Workers AI
  ├─ NVIDIA NIM            ├─ ZenMux API
  ├─ Groq API              └─ Ollama Cloud API
  ├─ OpenRouter API
```

**[→ Full Architecture](ARCHITECTURE.md)**

## License

WTFPL (Do What The Fuck You Want To Public License)

## Support

For issues, questions, or contributions:
- Check **[AGENTS.md](AGENTS.md)** for quick reference
- Review **[TESTING.md](TESTING.md)** for test architecture
- See **[ARCHITECTURE.md](ARCHITECTURE.md)** for design details