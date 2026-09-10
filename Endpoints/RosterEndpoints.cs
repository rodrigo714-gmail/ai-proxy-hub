/// <summary>
/// Model-roster renewal endpoints.
///
/// <c>GET /api/roster/diff</c> answers "what would the sync change?" without changing anything —
/// the diff is computed from a fresh discovery pass per provider.
/// <c>POST /api/roster/sync</c> runs a cycle now; with <c>?apply=true</c> it writes the config
/// even while <c>ROSTER_MODE=observe</c>, which is the human-in-the-loop path:
/// inspect the diff, apply it, restart nothing.
///
/// These endpoints report and mutate the provider roster, which is not sensitive the way
/// <c>/api/usage</c> is (no spend, no key metadata) — but they can rewrite config files, so
/// <c>POST</c> is the only one that takes effect, and the auth middleware still guards it when
/// <c>PROXY_API_KEY</c> is set.
/// </summary>
internal static class RosterEndpoints
{
    internal static IEndpointRouteBuilder MapRosterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/roster/diff", async (ModelRosterSyncService roster, ModelCatalogService catalog, CancellationToken ct) =>
        {
            if (roster.Mode == ModelRosterSyncService.RosterMode.Off)
            {
                return Results.Json(new { mode = "off", message = "ROSTER_MODE=off — roster sync is disabled." }, JsonDefaults.SnakeCase);
            }

            IReadOnlyList<ModelRosterSyncService.RosterProviderDiff> diffs = await roster.ComputeAllDiffsAsync(ct);
            return Results.Json(new
            {
                mode = roster.Mode.ToString().ToLowerInvariant(),
                interval_minutes = (int)roster.Interval.TotalMinutes,
                retire_after = roster.RetireAfter,
                auto_enable_additions = roster.AutoEnable,
                last_cycle_utc = catalog.ModelsLastRefreshUtc,
                providers = diffs.Select(d => new
                {
                    provider = d.Provider,
                    display_name = ProviderCapabilitiesRegistry.DisplayName(d.Provider),
                    observed = d.ObservedCount,
                    enabled_in_config = d.EnabledCount,
                    note = d.Note,
                    changes = d.Changes.Select(c => new
                    {
                        model = c.Model,
                        kind = c.Kind.ToString().ToLowerInvariant(),
                        reason = c.Reason
                    })
                })
            }, JsonDefaults.SnakeCase);
        });

        app.MapPost("/api/roster/sync", async (ModelRosterSyncService roster, ModelCatalogService catalog, bool apply = false, CancellationToken ct = default) =>
        {
            if (roster.Mode == ModelRosterSyncService.RosterMode.Off)
            {
                return Results.Json(new { mode = "off", message = "ROSTER_MODE=off — roster sync is disabled." }, JsonDefaults.SnakeCase);
            }

            ModelRosterSyncService.RosterCycleResult result = await roster.RunCycleAsync(catalog, ct, force: apply);
            return Results.Json(new
            {
                mode = result.Mode.ToString().ToLowerInvariant(),
                applied = apply || result.Mode == ModelRosterSyncService.RosterMode.Sync,
                at_utc = result.AtUtc.UtcDateTime.ToString("o"),
                files_written = result.FilesWritten,
                available_models = catalog.AvailableModels.Length,
                providers = result.Diffs.Select(d => new
                {
                    provider = d.Provider,
                    observed = d.ObservedCount,
                    enabled_in_config = d.EnabledCount,
                    note = d.Note,
                    changes = d.Changes.Select(c => new
                    {
                        model = c.Model,
                        kind = c.Kind.ToString().ToLowerInvariant(),
                        reason = c.Reason
                    })
                })
            }, JsonDefaults.SnakeCase);
        });

        return app;
    }
}
