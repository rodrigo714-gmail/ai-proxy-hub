# Copilot Instructions

La fuente de verdad para agentes es [`CLAUDE.md`](../CLAUDE.md) (reglas duras, comandos y
gotchas). Este archivo solo agrega lo específico de Copilot y repite las restricciones que más
se rompen.

## Reglas duras

- **Nunca tocar `main`** (rama protegida). Todo el trabajo va a `develop`; ramas de feature desde
  `develop` y merge de vuelta a `develop` con Conventional Commits.
- Actualizar `CHANGELOG.md` en cada merge: sección fechada, estilo narrativo (qué rompió, por qué,
  qué lo arregló), más nuevo arriba.

## Comandos

```bash
dotnet build          # .NET 10
dotnet test           # suite offline completa (usa stub in-process, sin red ni keys reales)
dotnet run            # puerto 11434 = puerto del daemon Ollama real; si Ollama corre, usar PROXY_PORT
```

Preferir `dotnet test --filter "FullyQualifiedName~<Clase>"` sobre correr toda la suite.

## Credenciales

Separar claramente las API keys: **no confundir la API key de Ollama Cloud
(`PROVIDER_OLLAMACLOUD_API_KEY`) con la clave del proxy local (`PROXY_API_KEY`)**. Las keys de
proveedores cloud viven en `.env` (nunca se comitea; solo se trackea `.env.example`).

## Testing

Todo test que construya un `ProviderRegistry` o toque variables de entorno del proceso DEBE estar
en `[Collection("Proxy")]` y usar `ProviderEnvScope`. Ignorar esto genera condiciones de carrera
con `.env` del desarrollador.

## Estilo

- Nullable habilitado, `ImplicitUsings`; sin dependencias NuGet de runtime (solo las de test).
- No reintroducir nombres viejos del proyecto (`vs2026-copilot-deepseek-v4`,
  `deepseek-copilot-proxy`); el ensamblado/solución se llaman `ai-proxy-hub`.