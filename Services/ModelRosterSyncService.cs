using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Keeps <c>config/model-selection/*.json</c> aligned with what each provider actually offers.
///
/// The proxy already <em>filters</em> against live discovery — a model that vanished upstream is
/// never served — but the roster itself is hand-curated, so two drifts accumulate silently:
/// a provider publishes a new model and no client ever sees it, and a retired model keeps
/// sitting in the config (and in <c>/api/tags</c>, which merges every enabled entry regardless of
/// discovery) as if it were still on offer.
///
/// This service observes the discovery lists the catalog already fetches, and turns them into a
/// proposed diff: ids that match no enabled entry are <em>additions</em>; enabled entries that no
/// observation matches are <em>retirements</em>. Retirement is deliberately slow for curated
/// entries — a provider's catalog endpoint can fail or truncate, and standing a good model down
/// on one bad observation is worse than carrying a stale entry for an hour — while entries the
/// sync itself created retire on the first miss, because nobody chose them by hand.
///
/// Three modes (<c>ROSTER_MODE</c>):
/// <list type="bullet">
///   <item><c>off</c> — inert.</item>
///   <item><c>observe</c> (default) — records observations and answers <c>/api/roster/diff</c>,
///     but never writes. Nothing changes about what is served.</item>
///   <item><c>sync</c> — additionally applies the diff to the config files on a timer, so the
///     offer renews itself across restarts and without a human in the loop.</item>
/// </list>
///
/// Additions land <em>disabled</em> unless <c>ROSTER_AUTO_ENABLE=true</c>: an unreviewed model id
/// from an aggregator's catalog is not something to hand to VS 2026 by default, but it is
/// something worth recording so the next curation pass starts from "enable these three" rather
/// than from a blank page.
/// </summary>
internal sealed class ModelRosterSyncService : BackgroundService, IModelRosterObserver
{
    internal enum RosterMode
    {
        Off,
        Observe,
        Sync
    }

    internal enum RosterChangeKind
    {
        /// <summary>New upstream id with no config entry at all.</summary>
        Add,

        /// <summary>A previously auto-retired entry is back; flip it to enabled.</summary>
        ReEnable,

        /// <summary>Enabled entry no longer offered by the provider.</summary>
        Retire,

        /// <summary>Enabled curated entry not seen yet — counted toward the retirement threshold.</summary>
        PendingRetire
    }

    internal sealed record RosterChange(string Provider, string Model, RosterChangeKind Kind, string Reason);

    internal sealed record RosterProviderDiff(
        string Provider,
        int ObservedCount,
        int EnabledCount,
        IReadOnlyList<RosterChange> Changes,
        string? Note);

    internal sealed class RosterCycleResult
    {
        public required DateTimeOffset AtUtc { get; init; }
        public required RosterMode Mode { get; init; }
        public required IReadOnlyList<RosterProviderDiff> Diffs { get; init; }
        public required IReadOnlyList<string> FilesWritten { get; init; }
        public bool Changed => FilesWritten.Count > 0;
    }

    private sealed class ProviderObservation
    {
        public string[] LastSeenIds = [];
        public DateTime LastSeenUtc { get; set; }
        // entry match -> consecutive checks in which the provider's list did not contain it
        public Dictionary<string, int> Misses { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ProviderRegistry _providerRegistry;
    private readonly ModelSelectionStore _store;
    private readonly IServiceProvider? _services;
    private readonly SemaphoreSlim _cycleLock = new(1, 1);

    private readonly ConcurrentDictionary<string, ProviderObservation> _observations =
        new(StringComparer.OrdinalIgnoreCase);

    private RosterCycleResult? _lastCycle;

    public ModelRosterSyncService(
        ProviderRegistry providerRegistry,
        ModelSelectionStore store,
        IServiceProvider? services = null)
    {
        _providerRegistry = providerRegistry;
        _store = store;
        _services = services;

        Mode = ReadMode();
        Interval = TimeSpan.FromMinutes(ReadInt("ROSTER_SYNC_INTERVAL_MINUTES", 60));
        RetireAfter = Math.Max(1, ReadInt("ROSTER_RETIRE_AFTER", 3));
        AutoEnable = ReadBool("ROSTER_AUTO_ENABLE");
        MaxAdditions = Math.Max(0, ReadInt("ROSTER_MAX_ADDITIONS", 10));
        _writeDirOverride = ReadTrimmed("ROSTER_WRITE_DIR");
        ProviderAllowlist = ReadAllowlist("ROSTER_PROVIDERS");

        if (Mode != RosterMode.Off)
        {
            LoadPersistedObservations();
        }
    }

    internal RosterMode Mode { get; }
    internal TimeSpan Interval { get; }
    internal int RetireAfter { get; }
    internal bool AutoEnable { get; }
    internal int MaxAdditions { get; }
    internal RosterCycleResult? LastCycle => _lastCycle;

    /// <summary>
    /// Test seam: when set, <see cref="WriteDirectories"/> returns <em>only</em> this directory,
    /// so a test cycle cannot touch the workspace or build-output config.
    /// </summary>
    internal string? WriteDirOverrideForTest
    {
        get => _writeDirOverride;
        set => _writeDirOverride = value;
    }

    // ── Observation (called by the catalog on every discovery pass) ──────────

    /// <summary>
    /// Records what a provider just published. An empty list is treated as "no signal" — a
    /// failed or rate-limited catalog fetch must not start retiring healthy models.
    /// </summary>
    public void Observe(string providerName, string[] discovered)
    {
        if (Mode == RosterMode.Off || discovered is null || discovered.Length == 0)
        {
            return;
        }

        ProviderObservation obs = _observations.GetOrAdd(providerName, _ => new ProviderObservation());
        lock (obs)
        {
            obs.LastSeenIds = discovered;
            obs.LastSeenUtc = DateTime.UtcNow;

            foreach (ModelSelectionEntry entry in _store.GetProviderModelSelections(providerName))
            {
                if (!entry.Enabled)
                {
                    continue;
                }

                bool seen = discovered.Any(id => id.Contains(entry.Match, StringComparison.OrdinalIgnoreCase));
                obs.Misses[entry.Match] = seen ? 0 : (obs.Misses.TryGetValue(entry.Match, out int n) ? n + 1 : 1);
            }
        }
    }

    internal int GetMissCount(string providerName, string match)
    {
        if (!_observations.TryGetValue(providerName, out ProviderObservation? obs))
        {
            return 0;
        }

        lock (obs)
        {
            return obs.Misses.TryGetValue(match, out int n) ? n : 0;
        }
    }

    // ── Diff ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares one provider's live list against its config entries. Pure with respect to
    /// arguments so it can be unit-tested without a registry or the network.
    /// </summary>
    internal static RosterProviderDiff ComputeDiff(
        string providerName,
        string[] observed,
        ModelSelectionEntry[] entries,
        Func<string, int> missCount,
        int retireAfter,
        int maxAdditions)
    {
        List<RosterChange> changes = [];

        if (observed.Length == 0)
        {
            return new RosterProviderDiff(providerName, 0,
                entries.Count(e => e.Enabled), [], "no live observation — nothing inferred");
        }

        // Additions / re-enables: an observed id that no enabled entry claims.
        // (Cast to nullable before FirstOrDefault: on a record *struct* array, FirstOrDefault
        // returns default — never null — so a plain `ModelSelectionEntry? x = list.First…()`
        // would always be non-null and additions would silently stop being proposed.)
        int additions = 0;
        foreach (string id in observed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            ModelSelectionEntry? claimed = entries
                .Where(e => e.Enabled && id.Contains(e.Match, StringComparison.OrdinalIgnoreCase))
                .Select(e => (ModelSelectionEntry?)e)
                .FirstOrDefault();

            if (claimed is not null)
            {
                continue;
            }

            // A disabled entry is only reversible if the sync itself retired it. Two other states
            // are disabled without being retired, and both must survive untouched:
            //  - a curator's deliberate enabled:false (EOL, not entitled, ToS) with its _comment;
            //  - a fresh auto-addition awaiting review, which is exactly why it landed disabled —
            //    re-enabling it on the next cycle would undo ROSTER_AUTO_ENABLE=false by itself.
            // _retired is written only by the retirement path below, so it is the single marker
            // that distinguishes "the sync stood this down and may stand it back up" from both.
            ModelSelectionEntry? retiredEntry = entries
                .Where(e => !e.Enabled && e.Retired && id.Contains(e.Match, StringComparison.OrdinalIgnoreCase))
                .Select(e => (ModelSelectionEntry?)e)
                .FirstOrDefault();
            if (retiredEntry is not null)
            {
                changes.Add(new RosterChange(providerName, retiredEntry.Value.Match, RosterChangeKind.ReEnable,
                    "back on the provider's catalog"));
                continue;
            }

            ModelSelectionEntry? leftDisabled = entries
                .Where(e => !e.Enabled && !e.Retired && id.Contains(e.Match, StringComparison.OrdinalIgnoreCase))
                .Select(e => (ModelSelectionEntry?)e)
                .FirstOrDefault();
            if (leftDisabled is not null)
            {
                continue;
            }

            if (ModelCatalogService.IsNonChatModel(id))
            {
                continue;
            }

            if (additions >= maxAdditions)
            {
                continue;
            }

            additions++;
            changes.Add(new RosterChange(providerName, id, RosterChangeKind.Add,
                "new id on the provider's catalog"));
        }

        // Retirements: an enabled entry no observed id matches.
        foreach (ModelSelectionEntry entry in entries.Where(e => e.Enabled))
        {
            bool seen = observed.Any(id => id.Contains(entry.Match, StringComparison.OrdinalIgnoreCase));
            if (seen)
            {
                continue;
            }

            int misses = missCount(entry.Match);
            int needed = entry.AutoManaged ? 1 : retireAfter;

            if (misses >= needed)
            {
                changes.Add(new RosterChange(providerName, entry.Match, RosterChangeKind.Retire,
                    entry.AutoManaged
                        ? "auto entry not offered for 1 check"
                        : $"not offered for {misses} consecutive checks (threshold {retireAfter})"));
            }
            else
            {
                changes.Add(new RosterChange(providerName, entry.Match, RosterChangeKind.PendingRetire,
                    $"miss {misses}/{needed}"));
            }
        }

        return new RosterProviderDiff(
            providerName,
            observed.Length,
            entries.Count(e => e.Enabled),
            changes,
            null);
    }

    /// <summary>
    /// Builds diffs for every active provider, using fresh live discovery where possible and
    /// the last recorded observation otherwise.
    /// </summary>
    internal async Task<IReadOnlyList<RosterProviderDiff>> ComputeAllDiffsAsync(CancellationToken ct)
    {
        List<RosterProviderDiff> diffs = [];

        foreach (ProviderInfo prov in _providerRegistry.Providers)
        {
            if (!IsTracked(prov.Name))
            {
                continue;
            }

            string[] observed = await ModelCatalogService.TryGetModelsFromProvider(prov, ct);
            if (observed.Length == 0 && _observations.TryGetValue(prov.Name, out ProviderObservation? cached))
            {
                lock (cached)
                {
                    observed = cached.LastSeenIds;
                }
            }

            diffs.Add(ComputeDiff(
                prov.Name,
                observed,
                _store.GetProviderModelSelections(prov.Name),
                match => GetMissCount(prov.Name, match),
                RetireAfter,
                MaxAdditions));
        }

        return diffs;
    }

    // ── Cycle: diff + (in sync mode) apply ───────────────────────────────────

    /// <summary>
    /// Runs one full cycle. Returns the cycle result; writes nothing unless
    /// <see cref="Mode"/> is <see cref="RosterMode.Sync"/> (pass <c>force: true</c> from the
    /// manual endpoint to apply while in observe mode).
    /// </summary>
    internal async Task<RosterCycleResult> RunCycleAsync(ModelCatalogService catalog, CancellationToken ct, bool force = false)
    {
        if (Mode == RosterMode.Off)
        {
            return new RosterCycleResult { AtUtc = DateTimeOffset.UtcNow, Mode = Mode, Diffs = [], FilesWritten = [] };
        }

        await _cycleLock.WaitAsync(ct);
        try
        {
            IReadOnlyList<RosterProviderDiff> diffs = await ComputeAllDiffsAsync(ct);
            List<string> written = [];

            bool apply = force || Mode == RosterMode.Sync;
            if (apply)
            {
                foreach (RosterProviderDiff diff in diffs)
                {
                    ct.ThrowIfCancellationRequested();
                    IReadOnlyList<RosterChange> actionable =
                        [.. diff.Changes.Where(c => c.Kind != RosterChangeKind.PendingRetire)];
                    if (actionable.Count == 0)
                    {
                        continue;
                    }

                    foreach (string file in ApplyChanges(diff.Provider, actionable, catalog))
                    {
                        written.Add(file);
                    }
                }
            }

            if (written.Count > 0)
            {
                _store.Reload();
                await catalog.RefreshAvailableModels(ct);
                Console.WriteLine($"[ROSTER] renewed the offer: {written.Count} config file(s) updated, " +
                    $"{catalog.AvailableModels.Length} models now published.");
            }

            SavePersistedObservations();

            _lastCycle = new RosterCycleResult
            {
                AtUtc = DateTimeOffset.UtcNow,
                Mode = Mode,
                Diffs = diffs,
                FilesWritten = written
            };
            return _lastCycle;
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    // ── Config file writing ──────────────────────────────────────────────────

    /// <summary>
    /// Applies additions/re-enables/retirements to the provider's config file in every
    /// candidate directory, so the source tree and the build output stay consistent (the
    /// loader merges both, and a stale copy in one would otherwise win the enabled-vs-disabled
    /// merge and undo a retirement).
    /// </summary>
    private IReadOnlyList<string> ApplyChanges(string providerName, IReadOnlyList<RosterChange> changes, ModelCatalogService catalog)
    {
        List<string> touched = [];

        foreach (string dir in WriteDirectories())
        {
            try
            {
                string? file = EnsureProviderFile(dir, providerName, changes, catalog);
                if (file is not null)
                {
                    touched.Add(file);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ROSTER] could not write '{providerName}' config into '{dir}': {ex.Message}");
            }
        }

        return touched;
    }

    private string? EnsureProviderFile(string dir, string providerName, IReadOnlyList<RosterChange> changes, ModelCatalogService catalog)
    {
        Directory.CreateDirectory(dir);
        string? path = FindProviderFileIn(dir, providerName)
            ?? Path.Combine(dir, $"{providerName}.json");

        JsonObject root;
        JsonArray models;
        if (File.Exists(path))
        {
            root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
            models = root["models"]?.AsArray() ?? [];
            root["models"] = models;
        }
        else
        {
            root = new JsonObject { ["provider"] = providerName };
            models = [];
            root["models"] = models;
        }

        root.TryAdd("provider", providerName);
        bool modified = false;
        int nextPriority = models
            .OfType<JsonObject>()
            .Select(m => m["priority"]?.GetValue<int>() ?? 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        foreach (RosterChange change in changes)
        {
            switch (change.Kind)
            {
                case RosterChangeKind.Add:
                    models.Add(BuildAddition(change, nextPriority++, catalog));
                    modified = true;
                    break;

                case RosterChangeKind.Retire:
                case RosterChangeKind.ReEnable:
                    JsonObject? target = models.OfType<JsonObject>()
                        .FirstOrDefault(o => string.Equals(o["match"]?.GetValue<string>(), change.Model, StringComparison.OrdinalIgnoreCase));
                    if (target is null)
                    {
                        break;
                    }

                    target["enabled"] = change.Kind == RosterChangeKind.ReEnable;
                    if (change.Kind == RosterChangeKind.Retire)
                    {
                        target["_retired"] = true;
                        target["_comment"] = $"roster sync {DateTime.UtcNow:yyyy-MM-dd}: retired — {change.Reason}";
                    }
                    else
                    {
                        // Re-enabling consumes the retirement: drop the marker so a later
                        // deliberate disable (or a fresh retirement) starts from a clean state.
                        target.Remove("_retired");
                        target["_comment"] = $"roster sync {DateTime.UtcNow:yyyy-MM-dd}: re-enabled — {change.Reason}";
                    }
                    modified = true;
                    break;
            }
        }

        if (!modified)
        {
            return null;
        }

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return path;
    }

    private JsonObject BuildAddition(RosterChange change, int priority, ModelCatalogService catalog)
    {
        (int ContextLength, int MaxOutputTokens, bool SupportsTools, bool SupportsVision, string[] _capabilities, string Family) profile =
            catalog.GetModelProfile(change.Model);

        JsonObject exec = new()
        {
            ["context_length"] = profile.ContextLength,
            ["max_output_tokens"] = profile.MaxOutputTokens,
            ["supports_tools"] = profile.SupportsTools,
            ["supports_vision"] = profile.SupportsVision,
            ["family"] = profile.Family,
            ["timeout_seconds"] = 120
        };

        return new JsonObject
        {
            ["match"] = change.Model,
            ["priority"] = priority,
            ["enabled"] = AutoEnable,
            ["_auto"] = true,
            ["_added"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["_comment"] = AutoEnable
                ? $"roster sync {DateTime.UtcNow:yyyy-MM-dd}: auto-added and enabled (ROSTER_AUTO_ENABLE)"
                : $"roster sync {DateTime.UtcNow:yyyy-MM-dd}: auto-added, disabled until reviewed",
            ["execution"] = exec
        };
    }

    // ── Persistence of observations ──────────────────────────────────────────

    private static string? ObservationsFilePath()
    {
        string dir = Environment.GetEnvironmentVariable("PROXY_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "model-roster.json");
        }
        catch
        {
            return null;
        }
    }

    private void LoadPersistedObservations()
    {
        string? path = ObservationsFilePath();
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("providers", out JsonElement providers))
            {
                return;
            }

            foreach (JsonProperty pv in providers.EnumerateObject())
            {
                ProviderObservation obs = new();
                if (pv.Value.TryGetProperty("last_seen_ids", out JsonElement ids) && ids.ValueKind == JsonValueKind.Array)
                {
                    obs.LastSeenIds = [.. ids.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!)];
                }

                if (pv.Value.TryGetProperty("last_seen_utc", out JsonElement seen) && seen.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(seen.GetString(), out DateTime seenUtc))
                {
                    obs.LastSeenUtc = seenUtc;
                }

                if (pv.Value.TryGetProperty("misses", out JsonElement misses) && misses.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty m in misses.EnumerateObject())
                    {
                        if (m.Value.ValueKind == JsonValueKind.Number)
                        {
                            obs.Misses[m.Name] = m.Value.GetInt32();
                        }
                    }
                }

                _observations[pv.Name] = obs;
            }
        }
        catch
        {
            // A corrupt observations file only costs history, never correctness.
        }
    }

    private void SavePersistedObservations()
    {
        string? path = ObservationsFilePath();
        if (path is null)
        {
            return;
        }

        try
        {
            JsonObject root = new()
            {
                ["schema_version"] = 1,
                ["updated_at"] = DateTimeOffset.UtcNow.ToString("o")
            };
            JsonObject providers = [];
            root["providers"] = providers;

            foreach ((string name, ProviderObservation obs) in _observations)
            {
                JsonObject single = new()
                {
                    ["last_seen_utc"] = obs.LastSeenUtc.ToString("o")
                };
                lock (obs)
                {
                    single["last_seen_ids"] = new JsonArray([.. obs.LastSeenIds.Select(x => JsonValue.Create(x))]);
                    JsonObject misses = [];
                    foreach ((string match, int count) in obs.Misses)
                    {
                        misses[match] = count;
                    }

                    single["misses"] = misses;
                }

                providers[name] = single;
            }

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Degrade silently: observations are also held in memory.
        }
    }

    // ── Hosted loop ──────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Mode == RosterMode.Off)
        {
            Console.WriteLine("  Roster sync: disabled (ROSTER_MODE=off).");
            return;
        }

        Console.WriteLine($"  Roster sync: mode={Mode}, interval={Interval.TotalMinutes:0} min, " +
            $"retire curated after {RetireAfter} misses, auto-enable additions={AutoEnable}.");

        // Let the first discovery pass populate observations before the first cycle.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_services?.GetService(typeof(ModelCatalogService)) is ModelCatalogService catalog)
                {
                    RosterCycleResult result = await RunCycleAsync(catalog, stoppingToken);
                    int pending = result.Diffs.Sum(d => d.Changes.Count(c => c.Kind == RosterChangeKind.PendingRetire));
                    int actionable = result.Diffs.Sum(d => d.Changes.Count(c => c.Kind != RosterChangeKind.PendingRetire));
                    if (result.Mode == RosterMode.Sync)
                    {
                        Console.WriteLine($"[ROSTER] cycle: {actionable} change(s) applied, {pending} pending, " +
                            $"{result.FilesWritten.Count} file(s) written.");
                    }
                    else if (actionable > 0)
                    {
                        Console.WriteLine($"[ROSTER] cycle (observe): {actionable} change(s) proposed, {pending} pending " +
                            "— set ROSTER_MODE=sync or POST /api/roster/sync to apply.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ROSTER] cycle failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ── Configuration helpers ────────────────────────────────────────────────

    private bool IsTracked(string providerName) =>
        ProviderAllowlist is null || ProviderAllowlist.Contains(providerName);

    private readonly IReadOnlySet<string>? ProviderAllowlist;
    private string? _writeDirOverride;

    private IEnumerable<string> WriteDirectories()
    {
        // An explicit override is exclusive: ROSTER_WRITE_DIR (and the test seam) exists so a
        // cycle can be pointed at a throwaway directory without ever touching the real config.
        if (!string.IsNullOrWhiteSpace(_writeDirOverride))
        {
            return [_writeDirOverride];
        }

        List<string> dirs = [];

        string? primary = ModelSelectionStore.ResolveSelectionDirectory();
        if (primary is not null)
        {
            dirs.Add(primary);
        }

        string cwdDir = Path.Combine(Directory.GetCurrentDirectory(), "config", "model-selection");
        dirs.Add(cwdDir);

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindProviderFileIn(string dir, string providerName)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("provider", out JsonElement provE)
                    && provE.ValueKind == JsonValueKind.String
                    && string.Equals(provE.GetString()?.Trim(), providerName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static RosterMode ReadMode()
    {
        string? raw = ReadTrimmed("ROSTER_MODE");
        return raw?.ToLowerInvariant() switch
        {
            "off" => RosterMode.Off,
            "sync" => RosterMode.Sync,
            _ => RosterMode.Observe
        };
    }

    private static string? ReadTrimmed(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(ReadTrimmed(name), out int v) ? v : fallback;

    private static bool ReadBool(string name)
    {
        string? raw = ReadTrimmed(name)?.ToLowerInvariant();
        return raw is "true" or "1" or "yes";
    }

    private static IReadOnlySet<string>? ReadAllowlist(string name)
    {
        string? raw = ReadTrimmed(name);
        if (raw is null)
        {
            return null;
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
