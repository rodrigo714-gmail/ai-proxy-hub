# AGENTS.md — Task Map for AI Assistants

Quick, task-oriented navigation. Hard constraints, build commands and testing gotchas live in
[`CLAUDE.md`](../CLAUDE.md) — read it first. Deep detail lives in the docs linked below; this
file only answers "where do I touch for X?".

**Project in one line:** ASP.NET Core minimal-API proxy bridging Copilot / Cursor / Continue /
VS 2026 BYOM / Ollama clients to 14 AI providers via OpenAI-compatible `/v1/*` and
Ollama-compatible `/api/*`.

## Where to look

| Task | Start here | Then read |
|---|---|---|
| Add a provider | `Services/ProviderCapabilitiesRegistry.cs` (one entry) + new `config/model-selection/{provider}.json` | `docs/CONFIGURATION.md` |
| Add / retire a model | `config/model-selection/{provider}.json` (`enabled`, `_comment`) | `docs/CONFIGURATION.md` §Model Roster Renewal |
| Change routing / collision | `Services/ProviderRegistry.cs` (`ResolveModel`, `ResolveCandidates`) | `docs/ARCHITECTURE.md` §Model Resolution |
| Change failover / cooldowns | `Infrastructure/UpstreamFailureClassifier.cs`, `Services/ProviderHealthService.cs` | `docs/ARCHITECTURE.md` §Failing Over |
| Change streaming | `Services/ChatStreamingService.cs`, `Services/OllamaResponseBuilder.cs` | `docs/ARCHITECTURE.md` §Streaming |
| Change parameter filtering | `Services/RequestTransformer.cs` + the model's `execution` block | `docs/CONFIGURATION.md` §Parameter Mapping |
| Roster auto-renewal | `Services/ModelRosterSyncService.cs` | `docs/CONFIGURATION.md` §Model Roster Renewal |
| Add an endpoint | `Endpoints/*.cs` + mapping in `Program.cs` | `docs/API.md` |
| Auth / dashboard carve-out | `Infrastructure/ProxyAuthenticationMiddleware.cs` | `docs/API.md` §Dashboard |
| Fix / write a test | `tests/ProxyTests/` — collection + env-scope rules are **mandatory** | `docs/TESTING.md` + `CLAUDE.md` §Testing gotchas |

## Service one-liners

```
ProviderCapabilitiesRegistry  single source of truth per provider: paths, base URL, env prefix
ProviderRegistry              model → provider resolution; candidate lists; @provider/@auto hints
ModelSelectionStore           loads config/model-selection/*.json (ordinal file order — see CLAUDE.md)
ModelCatalogService           live discovery per provider; builds AvailableModels + @auto aliases
ModelRosterSyncService        diff of discovery vs config; renews the JSON (ROSTER_MODE)
RequestTransformer            injects execution defaults, strips unsupported params, force-mode
ChatStreamingService          SSE ↔ NDJSON streaming both directions
OllamaResponseBuilder         OpenAI → Ollama response/tag shapes
UpstreamFailureClassifier     what a 4xx/5xx means (rate limit vs quota vs bad request)
ProviderHealthService         cooldown windows + candidate reordering (degrade, never exclude)
UsageRollupStore / UsageTracker / FreeTierCatalogStore   quota accounting behind /usage & /api/free-tier
```

## Endpoint map

| File | Surface |
|---|---|
| `Endpoints/OpenAiEndpoints.cs` | `GET /v1/models`, `POST /v1/chat/completions` |
| `Endpoints/OllamaEndpoints.cs` | `GET /api/version`, `/api/tags`, `POST /api/show`, `POST /api/chat` |
| `Endpoints/HealthEndpoints.cs` | `GET /health`, `/api/resilience/*` |
| `Endpoints/RosterEndpoints.cs` | `GET /api/roster/diff`, `POST /api/roster/sync` |
| `Endpoints/DashboardEndpoints.cs` | `/dashboard`, `/api/usage`, `/api/billing` |
| `Endpoints/FreeTierEndpoints.cs` | `/api/free-tier/summary` |
| `Endpoints/UsageEndpoints.cs` | `/usage*` |
| `Endpoints/ResponsesEndpoints.cs` | dead code — never mapped |

## Debugging recipes

```bash
# Which provider actually served, and after how many attempts?
curl -s -D - http://localhost:11434/v1/chat/completions -H "Content-Type: application/json" \
  -d '{"model":"deepseek-v4-pro","messages":[{"role":"user","content":"hi"}]}' | head -20
# → X-Proxy-Requested-Model / -Resolved-Model / -Provider / -Candidate-Index / -Attempts

# What would the roster sync change, without changing anything?
curl -s http://localhost:11434/api/roster/diff | jq '.providers[] | {provider, changes}'

# Model not offered? Order: enabled flag in config → discovery list → ctx==0 blacklist.
# Model routes to the wrong provider? Check the @provider pin, then longest-match specificity.
```

## Volatile data — do not duplicate here

Provider lists, per-model rosters, free-tier figures and quota numbers change constantly and are
**never** mirrored into agent docs (they go stale and mislead). Read them from their source:

- provider list & models: `ProviderCapabilitiesRegistry.cs` and `config/model-selection/*.json`
- free-tier allowances: `config/free-tier/catalog.json`
- what changed and why, newest first: `CHANGELOG.md`

## Docs index

- [`CLAUDE.md`](../CLAUDE.md) — constraints, commands, gotchas (loaded every session, keep it short)
- `docs/ARCHITECTURE.md` — request lifecycle, aliases, failover, streaming, error handling
- `docs/CONFIGURATION.md` — env vars, model-entry schema, roster renewal
- `docs/TESTING.md` — fixtures, collections, how to add tests
- `docs/API.md` · `docs/DEPLOYMENT.md`
