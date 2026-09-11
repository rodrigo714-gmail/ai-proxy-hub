# Configuration Guide

Complete configuration documentation for the multi-provider proxy supporting DeepSeek, OpenAI, NVIDIA NIM, Groq, OpenRouter, Moonshot/Kimi, Cerebras, Ollama Cloud, and ZenMux.

## Table of Contents

- [Environment Setup](#environment-setup)
- [Provider Configuration](#provider-configuration)
- [Model Selection & Defaults](#model-selection--defaults)
- [Qualified Model Aliases](#qualified-model-aliases)
- [Diagnostic Response Headers](#diagnostic-response-headers)
- [Parameter Mapping](#parameter-mapping)
- [Context Window Specifications](#context-window-specifications)
- [Free-Tier Catalog](#free-tier-catalog-configfree-tiercatalogjson)
- [Persistent Usage](#persistent-usage-datausage-rollupjson)
- [Advanced Configuration](#advanced-configuration)

---

## Environment Setup

### Required Environment Variables

Provider API keys must be set as environment variables. The proxy reads from, in order of
precedence:

1. System environment variables
2. `.env` file (loaded by `Program.cs` if present in the project root)
3. `appsettings.json`
4. Built-in defaults (port `11434`, model `deepseek-v4-pro`)

**Discovery order** (the tie-break when two providers offer the same model at equal priority):
`deepseek, openai, moonshot, google, cerebras, zai, mistral, siliconflow, cloudflare, nvidia,
openrouter, groq, zenmux, ollama`

A provider is only registered when its API key is present. Cloudflare additionally requires
`PROVIDER_CLOUDFLARE_BASE_URL`; without it the provider is skipped and a line is logged saying so.

### Provider Configuration

Each provider requires an API key and optionally a custom base URL:

```bash
# ── DeepSeek ───────────────────────────────────────────────────────
PROVIDER_DEEPSEEK_API_KEY=sk-your-deepseek-key
PROVIDER_DEEPSEEK_BASE_URL=https://api.deepseek.com

# ── OpenAI ─────────────────────────────────────────────────────────
PROVIDER_OPENAI_API_KEY=sk-your-openai-key
PROVIDER_OPENAI_BASE_URL=https://api.openai.com

# ── NVIDIA NIM ─────────────────────────────────────────────────────
PROVIDER_NVIDIA_API_KEY=nvapi-your-nvidia-key
PROVIDER_NVIDIA_BASE_URL=https://integrate.api.nvidia.com

# ── OpenRouter ─────────────────────────────────────────────────────
PROVIDER_OPENROUTER_API_KEY=sk-or-v1-your-key
PROVIDER_OPENROUTER_BASE_URL=https://openrouter.ai/api

# ── Groq ───────────────────────────────────────────────────────────
PROVIDER_GROQ_API_KEY=gsk_your-groq-key
PROVIDER_GROQ_BASE_URL=https://api.groq.com/openai

# ── Ollama Cloud ───────────────────────────────────────────────────
PROVIDER_OLLAMACLOUD_API_KEY=your-ollama-cloud-key

# ── Moonshot/Kimi ──────────────────────────────────────────────────
PROVIDER_MOONSHOT_API_KEY=sk-your-moonshot-key
PROVIDER_MOONSHOT_BASE_URL=https://api.moonshot.ai

# ── Cerebras ───────────────────────────────────────────────────────
PROVIDER_CEREBRAS_API_KEY=csk-your-cerebras-key
PROVIDER_CEREBRAS_BASE_URL=https://api.cerebras.ai

# ── ZenMux ─────────────────────────────────────────────────────────
PROVIDER_ZENMUX_API_KEY=your-zenmux-key
PROVIDER_ZENMUX_BASE_URL=https://zenmux.ai/api

# ── Free-tier providers ────────────────────────────────────────────
# Mistral: ~1B tokens/month but only ~2 RPM — a fallback, not a primary.
PROVIDER_MISTRAL_API_KEY=your-mistral-key
PROVIDER_MISTRAL_BASE_URL=https://api.mistral.ai

# SiliconFlow: free models capped at 50 req/day without purchased credit.
PROVIDER_SILICONFLOW_API_KEY=sk-your-siliconflow-key
PROVIDER_SILICONFLOW_BASE_URL=https://api.siliconflow.com

# Cloudflare Workers AI: 10k neurons/day, resets 00:00 UTC.
# BASE_URL IS MANDATORY — it embeds your account id and has no usable default.
PROVIDER_CLOUDFLARE_API_KEY=your-cloudflare-api-token
PROVIDER_CLOUDFLARE_BASE_URL=https://api.cloudflare.com/client/v4/accounts/YOUR_ACCOUNT_ID/ai/v1

# ── General ────────────────────────────────────────────────────────
DEEPSEEK_MODEL=deepseek-v4-pro
PROXY_PORT=11434
PROXY_API_KEY=              # optional: set to require auth on the proxy
PROXY_DASHBOARD_PUBLIC=     # optional: "false" also puts /dashboard behind the token
PROXY_DATA_DIR=             # optional: where usage-rollup.json lives (default ./data)
```

### Base URLs

| Provider | Default Base URL | Notes |
|----------|-----------------|-------|
| DeepSeek | `https://api.deepseek.com` | - |
| OpenAI | `https://api.openai.com` | - |
| NVIDIA NIM | `https://integrate.api.nvidia.com` | - |
| OpenRouter | `https://openrouter.ai/api` | OpenAI-compatible |
| Groq | `https://api.groq.com/openai` | OpenAI-compatible |
| Ollama Cloud | `https://ollama.com` | Ollama API format |
| Moonshot/Kimi | `https://api.moonshot.ai` | - |
| Cerebras | `https://api.cerebras.ai` | - |
| Google | `https://generativelanguage.googleapis.com` | OpenAI-compatible surface under `/v1beta/openai` |
| Z.AI | `https://api.z.ai/api/paas/v4` | Version lives in the base URL, so its chat/models paths are relative |
| ZenMux | `https://zenmux.ai/api` | Multi-model aggregator |
| Mistral | `https://api.mistral.ai` | 🆓 free "Experiment" tier |
| SiliconFlow | `https://api.siliconflow.com` | 🆓 free models; the `.cn` platform needs a Chinese phone number |
| Cloudflare Workers AI | *(none — must be set)* | 🆓 `…/accounts/{account_id}/ai/v1`; relative chat/models paths |

> Only providers with configured API keys are active at runtime. Set `PROVIDER_*_BASE_URL` to override the default (e.g., for self-hosted or region-specific endpoints).
>
> **Relative paths are deliberate.** `ProviderHttpClientFactory` appends a trailing slash to every
> base URL, so a provider whose URL carries a path segment (Z.AI's `/api/paas/v4`, Groq's
> `/openai`, Cloudflare's account id) keeps it. A leading slash on `ChatPath` would resolve against
> the host root and silently drop that segment — `ProviderCapabilitiesRegistryTests` guards it.

---

## Model Selection & Defaults

### Configuration Files

Model metadata lives in `config/model-selection/{provider}.json`:

```
config/model-selection/
├── deepseek.json       # DeepSeek models
├── openai.json         # OpenAI models
├── nvidia.json         # NVIDIA NIM models
├── groq.json           # Groq models
├── openrouter.json     # OpenRouter models
├── moonshot.json       # Moonshot/Kimi models
├── cerebras.json       # Cerebras models
├── ollamacloud.json    # Ollama Cloud models
├── ollama.json         # Ollama Cloud roster (single file for the 'ollama' provider)
└── zenmux.json         # whole roster disabled 2026-07-31 (HTTP 402 reject_no_credit)
```

### Model Entry Format

```json
{
  "provider": "ollama",
  "models": [
    {
      "match": "glm-5.2",
      "priority": 2,
      "enabled": true,
      "execution": {
        "context_length": 1000000,
        "max_output_tokens": 65536,
        "supports_tools": true,
        "supports_vision": false,
        "family": "glm",
        "temperature": 0.2,
        "top_p": 0.9,
        "max_tokens": 16384,
        "timeout_seconds": 240
      }
    }
  ]
}
```

### Field Reference

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `provider` | string | Yes | Provider name (must match registry) |
| `models[].match` | string | Yes | Model name substring to match |
| `models[].priority` | int | Yes | Priority order (lower = higher priority) |
| `models[].enabled` | bool | Yes | Include in model lists |
| `models[].execution.context_length` | int | No | Max context window in tokens |
| `models[].execution.max_output_tokens` | int | No | Max output tokens |
| `models[].execution.supports_tools` | bool | No | Tool calling support |
| `models[].execution.supports_vision` | bool | No | Vision/image input support |
| `models[].execution.family` | string | No | Model family name |
| `models[].execution.temperature` | float | No | Default temperature |
| `models[].execution.top_p` | float | No | Default top_p |
| `models[].execution.max_tokens` | int | No | Default max_tokens |
| `models[].execution.reasoning_effort` | string | No | "low", "medium", "high" |
| `models[].execution.timeout_seconds` | int | No | Request timeout |
| `models[].execution.override_client_params` | bool | No | Force-override client values |
| `models[].execution.uses_max_completion_tokens` | bool | No | Send the budget as `max_completion_tokens` (OpenAI GPT-5.x / o-series) |
| `models[].execution.supports_temperature` | bool | No | When `false`, strip `temperature` and `top_p` entirely |
| `models[].execution.supports_reasoning` | bool | No | Advertise reasoning capability in `/api/tags` |
| `models[].upstream` | string | No | Upstream id when it differs from `match` |
| `models[]._auto` | bool | No | Set by the roster sync on entries it generated. Auto entries retire on the first observed absence; curated entries need `ROSTER_RETIRE_AFTER` consecutive misses. |
| `models[]._added` | string | No | Date the roster sync added the entry (`YYYY-MM-DD`). |
| `models[]._retired` | bool | No | Written **only** by the sync when it retires an entry. It marks a disable as the sync's own and reversible: if the model reappears on the provider's catalog, the sync re-enables it. A disabled entry *without* this flag is either a curator's deliberate `enabled:false` or a fresh addition awaiting review — the sync leaves both alone. |
| `models[]._keep_enabled` | bool | No | Exempts an enabled entry from catalog-absence retirement. For models that answer real requests while `/v1/models` no longer lists them (DeepSeek's `deepseek-v4-flash` did through 219 consecutive misses). Record the live verification in `_comment`. |
| `models[]._comment` | string | No | Why an entry is disabled — a retirement reason written by the sync, or a curator's note (EOL, not entitled, ToS). |

### Override Client Params

When `override_client_params: true`, the proxy overwrites client-supplied values for `temperature`, `top_p`, `max_tokens`, and `reasoning_effort` with the configured values.

Currently enabled for:
- Moonshot `kimi-k2.7-code`, `kimi-k2.7-code-highspeed`, `kimi-k2.6` (Kimi K2.x mandates `temperature=1.0`)
- Ollama Cloud `kimi-k2.7-code` (same rule)

---

## Model Roster Renewal (`ModelRosterSyncService`)

Discovery already filters: a model that vanished upstream is never served. But the roster files
themselves are hand-curated, so two drifts accumulate silently — a provider publishes a new model
and no client ever sees it, and a retired model keeps sitting in the config (and in `/api/tags`,
which merges every enabled entry regardless of discovery) as if it were still on offer.

The roster sync observes the same discovery lists the catalog fetches and turns them into a
proposed diff: ids that match no enabled entry are **additions**; enabled entries that no
observation matches are **retirements**.

### Modes (`ROSTER_MODE`)

| Value | Behaviour |
|-------|-----------|
| `off` | Inert. |
| `observe` (default) | Records observations and answers `GET /api/roster/diff`, but **never writes**. Nothing changes about what is served. |
| `sync` | On a timer, additionally applies the diff to `config/model-selection/*.json` and reloads the catalog — the offer renews itself without a restart. |

### Endpoints

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/api/roster/diff` | GET | What would the sync change? Computed from a fresh discovery pass per provider. |
| `/api/roster/sync` | POST | Runs a cycle now. `?apply=true` writes the config even while in `observe` mode — the human-in-the-loop path: inspect the diff, apply it, restart nothing. |

### Other settings

| Variable | Default | Meaning |
|----------|---------|---------|
| `ROSTER_SYNC_INTERVAL_MINUTES` | `60` | How often the timer runs a cycle in `sync` mode. |
| `ROSTER_RETIRE_AFTER` | `3` | Consecutive checks an enabled **curated** entry must be absent before it is retired. A provider's catalog endpoint can fail or truncate; standing a good model down on one bad observation is worse than carrying a stale entry. |
| `ROSTER_AUTO_ENABLE` | `false` | When `false`, additions land **disabled** with a `_comment` so the next curation pass starts from "enable these" rather than a blank page. An unreviewed id from an aggregator's catalog is not something to hand to VS 2026 by default. |
| `ROSTER_MAX_ADDITIONS` | `10` | Cap on auto-additions per provider per cycle. |
| `ROSTER_PROVIDERS` | *(all)* | Comma-separated allowlist; only these providers are considered. |
| `ROSTER_WRITE_DIR` | *(config dir)* | Where to write. Point it at a throwaway directory to dry-run writes. |

### What it deliberately does NOT do

- **Never retires on an empty observation.** A failed or rate-limited `/v1/models` fetch is "no
  signal", not "everything is gone".
- **Never touches an entry it did not retire.** A disabled entry without `_retired` is either the
  curator's deliberate `enabled:false` (end-of-life, not entitled, ToS — a decision made with more
  information than the catalog has) or a fresh addition awaiting review. The next cycle leaves both
  exactly as they are, which is what keeps `ROSTER_AUTO_ENABLE=false` from being undone by the sync
  itself one hour later.
- **Never proposes non-chat ids** (embeddings, guardrails, moderation, rerankers, ASR/TTS, world
  models).
- Auto entries (`_auto: true`) retire on the **first** miss — nobody chose them by hand.

---

## Qualified Model Aliases

The `/api/tags` endpoint now returns `model` fields in the format `model@provider:latest` (e.g., `deepseek-v4-pro@ollama:latest`). This qualified alias ensures that when the client sends this model name back in a request, the proxy routes it to the **correct specific provider** instead of falling back to the default provider (DeepSeek).

**How it works:**
1. `/api/tags` returns: `"model": "deepseek-v4-pro@ollama:latest"`
2. Client sends: `{"model": "deepseek-v4-pro@ollama:latest"}`
3. `ProviderRegistry.ResolveModel()` strips `:latest` → `deepseek-v4-pro@ollama`
4. The `@ollama` suffix forces routing to the Ollama provider (no failover)

**For bare model names** (e.g., `deepseek-v4-pro`), the proxy resolves to the lowest-priority claimant provider based on discovery order. To pin a specific provider, use the qualified `model@provider` form.

**To route across providers instead of pinning one**, use the `model@auto` form that `/api/tags`
publishes alongside each pinned entry as `AUTO - <model>`:

1. `/api/tags` returns `"model": "gpt-oss-120b@auto:latest"`
2. `ResolveCandidates` returns every provider serving that model, ordered by configured priority
3. `ResolveRoutePlan` moves any provider in cooldown to the back
4. The walk stops at the first provider that answers

`auto` is reserved and can never be a provider name. AUTO entries exist only for models served by
two or more configured providers, and they advertise the smallest context and output limit among
their candidates rather than the largest.

---

## Diagnostic Response Headers

Both endpoints include response headers for debugging routing decisions:

| Header | Source | Description |
|--------|--------|-------------|
| `X-Proxy-Requested-Model` | Both | What the client sent |
| `X-Proxy-Resolved-Model` | Both | Internal model after resolution |
| `X-Proxy-Upstream-Model` | Both | Model sent to upstream API |
| `X-Proxy-Provider` | Both | Provider that handled the request |
| `X-Proxy-Candidate-Count` | Both | How many providers could have served this model |
| `X-Proxy-Candidate-Index` | Both | Position of the provider that answered; non-zero means it failed over |
| `X-Proxy-Attempts` | Both | How many candidates were actually tried |
| `X-Proxy-Primary-Provider` | `/v1/*` | Primary candidate provider |
| `X-Proxy-Primary-Upstream` | `/v1/*` | Primary upstream model |

---

## Parameter Mapping

The following table shows which parameters are supported by each provider:

| Provider | temperature | top_p | top_k | reasoning_effort | tools |
|----------|:-----------:|:-----:|:-----:|:-----------------:|:-----:|
| DeepSeek | ✅ | ⚠️ omitted w/ reasoning | ❌ | ✅ | ✅ |
| OpenAI | ✅ | ⚠️ omitted w/ reasoning | ❌ | ✅ (o-series) | ✅ |
| NVIDIA NIM | ✅ | ✅ | ✅ | ❌ | ✅ |
| Groq | ✅ | ✅ | ✅ | ❌ | ✅ (except `compound`/`compound-mini`) |
| OpenRouter | ✅ | ✅ | ✅ | ❌ (passthrough) | ✅ |
| Ollama Cloud | ✅ | ✅ | ✅ | ❌ | ✅ |
| Moonshot/Kimi | ✅ | ✅ | ❌ | ❌ | ✅ |
| Cerebras | ✅ | ✅ | ✅ | ❌ | ✅ |
| ZenMux | ✅ | ✅ | ❌ | ❌ | ✅ |

**Key rules:**
- `reasoning_effort` is only for DeepSeek and OpenAI o-series. Filtered for all others.
- `top_k` is removed for DeepSeek, OpenAI, Moonshot/Kimi, and ZenMux.
- `top_p` is omitted when `reasoning_effort` is set (DeepSeek/OpenAI rule).
- `tools`/`tool_choice` are forwarded to every provider, but **stripped per model** when the
  model's `execution.supports_tools` is `false`. Groq's `compound`/`compound-mini` are the live
  case: they run Groq's own server-side tools and answer HTTP 400 to any client tools payload.
- OpenAI `gpt-5.5` deliberately has **no** `reasoning_effort` default: OpenAI answers 400
  "Function tools with reasoning_effort are not supported for gpt-5.5" when an agent-mode
  client sends tools alongside it.

---

## Context Window Specifications

Enabled models by provider:

| Provider | # Enabled | Models |
|----------|:---------:|--------|
| DeepSeek | 2 | deepseek-v4-pro (1M ctx), deepseek-v4-flash (1M ctx) |
| OpenAI | 4 | gpt-5.5 (400K), gpt-5.4 (400K), gpt-5.4-mini (200K), o4-mini (200K) |
| Google | 8 | gemini-3.5-flash (1M), gemini-3.1-pro-preview (2M), gemini-3-pro-preview (2M), gemini-3-flash-preview (1M), gemini-3.1-flash-lite (1M), gemini-2.5-pro (2M), gemini-2.5-flash (1M), gemini-2.5-flash-lite (1M) |
| NVIDIA NIM | 8 | nemotron-3-super-120b (1M), glm-5.2 (200K), deepseek-v4-pro (1M), gpt-oss-120b (131K), nemotron-3-ultra-550b (128K), minimax-m3 (1M), llama-3.3-nemotron-super-49b (131K), llama-3.3-70b-instruct (128K) |
| Groq | 7 | qwen3.6-27b (131K), gpt-oss-120b (131K), gpt-oss-20b (131K), llama-3.3-70b-versatile (131K), llama-3.1-8b-instant (131K), compound (131K), compound-mini (131K) |
| OpenRouter | 10 | claude-sonnet-4.6 (1M), gpt-5.4 (400K), gemini-3.5-flash (1M), deepseek-v4-pro (1M), qwen3.7-plus (1M), qwen3-coder (1M), kimi-k2.7-code (262K), kimi-k2.6 (262K), grok-4.3 (1M), nemotron-3-super-120b (1M) |
| Moonshot/Kimi | 6 | kimi-k2.7-code (262K), kimi-k2.7-code-highspeed (262K), kimi-k2.6 (262K), moonshot-v1-128k (131K), moonshot-v1-auto (131K), moonshot-v1-32k (32K) |
| Cerebras | 2 | zai-glm-4.7 (128K), gpt-oss-120b (131K) |
| Z.AI | 5 | glm-5.2 (1M), glm-5.1 (1M), glm-4.7 (131K), glm-4.7-flash (131K, 🆓), glm-4.7-flashx (131K) |
| Ollama Cloud | 9 | kimi-k2.7-code (262K), glm-5.2 (1M), deepseek-v4-pro (1M), minimax-m3 (1M), nemotron-3-ultra (1M), nemotron-3-super (1M), glm-5.1 (1M), deepseek-v4-flash (1M), gpt-oss:120b (131K) |
| ZenMux | 0 | *whole roster disabled 2026-07-31 — every model answered HTTP 402 `reject_no_credit`; top up and re-enable in `zenmux.json`* |

---

## Free-Tier Catalog (`config/free-tier/catalog.json`)

Records what each provider gives away, on what cadence, under what request limits, and how
comfortable its terms are with a self-hosted personal proxy. It is data rather than C# so a figure
can be re-verified and corrected without a rebuild — free tiers change constantly.

```jsonc
{
  "schema_version": 1,
  "curated_at": "2026-07-31",          // a literal, NOT the file mtime: a deploy that rewrites
                                        // timestamps would report a months-old catalog as fresh
  "providers": [
    {
      "provider": "mistral",
      "pool_key": "mistral-free",       // providers sharing a budget share a pool_key
      "free_type": "recurring-monthly",
      "monthly_tokens": 1000000000,
      "requests_per_minute": 2,
      "tos": "caution",
      "tos_note": "Consumer terms scope API use to personal needs.",
      "signup_url": "https://console.mistral.ai",
      "source_url": "https://docs.mistral.ai/deployment/laplateforme/tier/",
      "verified_at": "2026-07-31",
      "notes": "Largest free pool of any provider, but ~2 RPM makes it a fallback, not a primary."
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `free_type` | `recurring-daily` · `recurring-monthly` · `recurring-uncapped` · `one-time-credit` · `none` |
| `pool_key` | Shared-budget key. Counted **once**, at its largest member. |
| `monthly_tokens` / `daily_tokens` | Published allowance. Daily is scaled by the length of the current month. |
| `credit_tokens` | One-time signup credit; totalled separately, never in the steady figure. |
| `requests_per_minute` / `requests_per_day` | Usually the *real* limit for a Copilot workload. |
| `tos` | `ok` · `caution` · `ambiguous` · `avoid` · `unknown` — informational, not legal advice. |
| `verified_at` | When the figure was last checked. Required; a number with no date is a number nobody can trust. |

`recurring-uncapped` providers are permanently free but publish no token cap. They are **listed and
never summed** — multiplying a rate limit by 24/7 produces a ceiling nobody reaches, which is
exactly how free-tier totals get inflated.

A malformed catalog logs a warning naming the file and yields **no** budget rather than silently
wrong numbers. Every registered provider must appear, even as `"free_type": "none"`, or
`FreeTierCatalogTests` fails.

---

## Persistent Usage (`data/usage-rollup.json`)

A per-day, per-provider aggregate written by `UsageSnapshotService` every 60 seconds and on
shutdown, so a **monthly** quota still means something after a restart. Location follows
`PROXY_DATA_DIR`, defaulting to `./data` next to the binary (git-ignored).

- Atomic writes (`.tmp` then move), so a crash mid-write cannot leave an unparseable file.
- 400-day retention, pruned on write.
- An unwritable directory degrades to memory-only with a warning; a corrupt file starts empty.
  Losing a statistic must never stop the proxy from serving requests.
- A hard kill (`taskkill /F`, container OOM) can lose up to 60 seconds of usage. A graceful stop
  (Ctrl+C, `docker stop`) flushes first.

It is a JSON rollup rather than SQLite on purpose: fourteen providers over a year is a few thousand
rows with a single writer, which does not justify the project's first native dependency.

---

## Advanced Configuration

### Local Ollama

The proxy can also connect to a local Ollama instance. Set these in `.env`:

```bash
PROVIDER_OLLAMA_BASE_URL=http://localhost:11434
```

This registers a second "ollama" provider that works without an API key. When both Ollama Cloud and local Ollama are configured, the `ollamacloud.json` and `ollama.json` configs are merged under the `ollama` provider key.

### Legacy DeepSeek Format

```bash
# Backward compatible — works without PROVIDER_ prefix
DEEPSEEK_API_KEY=sk-your-key
DEEPSEEK_BASE_URL=https://api.deepseek.com
```

### Proxy Authentication

```bash
PROXY_API_KEY=your-proxy-key        # Requires a bearer token
PROXY_DASHBOARD_PUBLIC=false        # …on the dashboard page too (default: page is public)
```

When `PROXY_API_KEY` is set, requests need `Authorization: Bearer <key>` or `X-Proxy-Key: <key>`.
`/dashboard`, `/vendor/` and `/health` are exempt by default, because a browser cannot attach a
bearer token to a plain navigation — open `/dashboard?key=YOUR_KEY` once and the page remembers it.

The **data** endpoints are never exempt: `/api/usage`, `/api/billing`, `/api/free-tier` and
`/api/resilience` expose spend, quota and key metadata. Prefix matching is path-boundary aware, so
`/dashboard-secret` is still rejected.

### Port Configuration

```bash
PROXY_PORT=8080                 # Default: 11434