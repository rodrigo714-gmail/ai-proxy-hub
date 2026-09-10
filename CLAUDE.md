# CLAUDE.md

Guidance for Claude Code (claude.ai/code) working in this repository. Keep this file short: it
is loaded into context every session, so it holds only what cannot be inferred from the code.
Deep detail lives in `docs/` — read those on demand instead of expanding this file.

**AI Proxy Hub** (GitHub: `rodrigo714-gmail/ai-proxy-hub`; assembly/csproj/solution all named
`ai-proxy-hub`) is an ASP.NET Core minimal-API proxy bridging GitHub Copilot, Cursor,
Continue.dev, Visual Studio 2026 BYOM and Ollama clients to 14 AI providers through two
surfaces: OpenAI-compatible `/v1/*` and Ollama-compatible `/api/*`. Primary workload: Copilot
code completion + chat in VS 2026. Older names (`vs2026-copilot-deepseek-v4`,
`deepseek-copilot-proxy`) are retired — never reintroduce them.

## Branching rule (hard constraint)

- **Never** touch `main` — it is the protected release branch.
- All work happens on `develop`. Feature branches off `develop`, merge back into `develop`.
- Conventional Commits are required for every merge into `develop`.
- Update `CHANGELOG.md` with every merge: dated section, narrative style — what broke, why,
  what fixed it. Newest entry goes on top.

## Build & Test

```bash
dotnet build                       # .NET 10.0, WebApplication.CreateSlimBuilder()
dotnet test                        # 599 tests, xUnit + WebApplicationFactory, fully offline
dotnet run                         # port 11434 by default

# Focused runs (prefer these over the whole suite)
dotnet test --filter "FullyQualifiedName~ModelRosterSyncTests"
dotnet test --filter "FullyQualifiedName~ParameterValidationTests"
dotnet test --filter TestMethodName=MySpecificTest

# Live smoke test of every published model, exactly as VS 2026 BYOM calls it
./scripts/test-all-providers.ps1            # non-streaming
./scripts/test-all-providers.ps1 -Stream    # streaming
```

**Port 11434 is the real Ollama daemon's port.** If Ollama is installed and running, the proxy
cannot bind and fails to start. Stop Ollama or set `PROXY_PORT`.

## Testing gotchas (ignoring these causes real bugs)

- Tests live in `tests/ProxyTests/`. Everything runs against an **in-process stub provider** —
  no network, no real API keys.
- Any test that constructs a `ProviderRegistry` or touches process env vars **must** be in
  `[Collection("Proxy")]`: `ProxyFixture` boots `Program.cs`, which loads the developer's real
  `.env` into the process, so anything running in parallel with it races.
- Those tests must also use `ProviderEnvScope`, which clears every `PROVIDER_*` variable derived
  from `ProviderCapabilitiesRegistry`. **Never hand-write the list** — a forgotten provider picks
  up a real key from `.env` and quietly changes collision resolution.
- The model-selection loader enumerates `config/model-selection/*.json` in **ordinal** order on
  purpose. Two files can declare the same model id at equal match length (e.g. `nvidia.json` and
  `openrouter.json` both list `nvidia/nemotron-3-super-120b-a12b`); the longest-match rule keeps
  the first seen on a tie, so an unsorted `EnumerateFiles` makes the winning provider depend on
  the filesystem — alphabetical on NTFS, arbitrary on ext4 — and turns Linux CI flaky. Keep it
  sorted.

## Credential separation

Per `.github/copilot-instructions.md`: **never confuse Ollama Cloud API keys with the local
proxy key.** Cloud provider keys are `.env` variables (`PROVIDER_DEEPSEEK_API_KEY`,
`PROVIDER_OLLAMACLOUD_API_KEY`, `PROVIDER_MOONSHOT_API_KEY`, …). The optional `PROXY_API_KEY`
guards access to the proxy itself and is unrelated. `.env` is gitignored and **never**
committed; only `.env.example` is tracked.

## Configuration model (non-obvious rules)

- Model metadata lives in `config/model-selection/{provider}.json`. The filename is cosmetic —
  the `"provider"` field inside binds it, and exactly one file may declare a given provider.
- **Matching is by substring, longest match first.** `"gpt-5.4"` is a substring of
  `"gpt-5.4-mini"`, so specificity — not `priority` — decides which entry a model gets. Priority
  only breaks ties between equally specific entries.
- `execution.override_client_params` (default `false`) force-overwrites the client's
  `temperature`/`top_p`/`max_tokens`/`reasoning_effort`. Moonshot Kimi K2.x mandates
  `temperature=1.0`.
- `execution.uses_max_completion_tokens` + `supports_temperature: false` — required by OpenAI
  GPT-5.x / o-series, which answer HTTP 400 to `max_tokens` or an explicit temperature.
- `"enabled": false` excludes a model from `/v1/models` and `/api/tags`. Retired/unentitled
  models stay in the JSON disabled with a `_comment` so nobody re-adds them. `_auto` marks
  entries the roster sync generated itself (they retire on the first observed miss; curated
  ones need `ROSTER_RETIRE_AFTER`).
- Adding a provider is a two-file change: one entry in `ProviderCapabilitiesRegistry` + its
  JSON. Nothing else — discovery, routing and filtering all read from that registry.

## Provider traps that bit us (verify before "fixing")

- **Cerebras `zai-glm-4.7`** is capped at **8192 tokens for messages + completion combined**,
  not the 128000 the roster once claimed. The cap is per-model, not account-wide — verify each
  entry against a live request before trusting a published figure.
- **Groq reports an over-TPM request as HTTP 413** with `rate_limit_exceeded` in the body, and
  charges `prompt + max_tokens` against the per-minute budget — keep `max_tokens` well under it.
- **Z.AI** puts its API under `https://api.z.ai/api/paas/v4` with a *relative* chat path;
  `ProviderHttpClientFactory` appends a trailing slash to every base URL, or `HttpClient`
  resolves relative paths against the last segment and silently drops `/v4`.
- **`/api/chat` must never emit `data:` frames** — an Ollama client silently discards them and
  the model looks silent. Reasoning models can spend the whole budget in
  `reasoning`/`reasoning_content` and return empty `content`; both directions fall back to the
  reasoning text.

## Roster renewal

`ModelRosterSyncService` keeps each provider's offer aligned with live discovery.
`ROSTER_MODE`: `off` | `observe` (default — proposes via `GET /api/roster/diff`, writes
nothing) | `sync` (applies the diff to the JSON on a timer and reloads without a restart).
`POST /api/roster/sync?apply=true` is the human-in-the-loop path. An empty discovery read is
'no signal' and never retires anything.

## Where to read more

- `docs/ARCHITECTURE.md` — DI graph, request lifecycle, alias resolution (`@provider` /
  `@auto`), streaming format conversion, failover/classifier/health, dashboard, upstream errors.
- `docs/CONFIGURATION.md` — every env var, the model-entry schema, parameter mapping, free-tier
  catalog, persistent usage, Model Roster Renewal.
- `docs/TESTING.md` · `docs/API.md` · `docs/DEPLOYMENT.md` · `docs/AGENTS.md` (task-oriented map).
- `CHANGELOG.md` — dated narrative of what changed and why, newest first.
