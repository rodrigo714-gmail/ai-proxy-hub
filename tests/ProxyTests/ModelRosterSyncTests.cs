using System.Text.Json.Nodes;
using ProxyTests.FakeProviders;
using Xunit;
using RosterProviderDiff = ModelRosterSyncService.RosterProviderDiff;
using RosterChange = ModelRosterSyncService.RosterChange;
using RosterChangeKind = ModelRosterSyncService.RosterChangeKind;
using RosterCycleResult = ModelRosterSyncService.RosterCycleResult;

namespace ProxyTests;

// The roster sync's decision logic (ComputeDiff) is a pure static function, so most of these
// tests drive it directly with hand-built entry arrays — no registry, no network. The two
// integration tests below use the same in-process FakeProviderHandler the catalog tests use,
// pointed at a throwaway config directory, so nothing here can touch the real config/model-selection.
//
// These tests mutate process env vars (ROSTER_* and PROVIDER_*), so they share the "Proxy"
// collection to run sequentially after ProxyFixture, exactly like ModelCatalogServiceTests.
[Collection("Proxy")]
public class ModelRosterSyncTests : IDisposable
{
    private readonly ProviderEnvScope _envScope = new();
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string name in new[] { "ROSTER_MODE", "ROSTER_RETIRE_AFTER", "ROSTER_AUTO_ENABLE", "ROSTER_MAX_ADDITIONS", "ROSTER_WRITE_DIR", "PROXY_DATA_DIR" })
            Environment.SetEnvironmentVariable(name, null);

        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        _envScope.Dispose();
    }

    private static ModelSelectionEntry Enabled(string match, int priority = 1) =>
        new(match, priority, true, new ModelExecutionConfig());

    private static ModelSelectionEntry Disabled(string match, int priority = 1, bool auto = false, bool retired = false) =>
        new(match, priority, false, new ModelExecutionConfig(), AutoManaged: auto, Retired: retired);

    private static ModelSelectionEntry AutoEnabled(string match, int priority = 1) =>
        new(match, priority, true, new ModelExecutionConfig(), AutoManaged: true);

    private static Func<string, int> Misses(params (string Match, int Count)[] values)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((string m, int c) in values) map[m] = c;
        return m => map.TryGetValue(m, out int c) ? c : 0;
    }

    // ── Additions ────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeDiff_NewUpstreamId_ProposesAdd()
    {
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "deepseek",
            ["deepseek-v4-pro", "deepseek-v5-turbo"],
            [Enabled("deepseek-v4-pro")],
            Misses(), retireAfter: 3, maxAdditions: 10);

        RosterChange add = Assert.Single(diff.Changes, c => c.Kind == RosterChangeKind.Add);
        Assert.Equal("deepseek-v5-turbo", add.Model);
    }

    [Fact]
    public void ComputeDiff_ObservedIdClaimedByEnabledEntry_IsNotAdded()
    {
        // "deepseek-v4-pro" is a substring of the observed id, so the entry already claims it.
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "deepseek",
            ["deepseek-v4-pro-2026-06-01"],
            [Enabled("deepseek-v4-pro")],
            Misses(), retireAfter: 3, maxAdditions: 10);

        Assert.DoesNotContain(diff.Changes, c => c.Kind == RosterChangeKind.Add);
    }

    [Fact]
    public void ComputeDiff_DeliberatelyDisabledCuratedEntry_IsNotReAdded()
    {
        // A curated entry left disabled with a _comment (EOL / not entitled) must not be
        // resurrected as an auto addition — the curator knew something the catalog does not.
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "deepseek",
            ["deepseek-legacy-eol"],
            [Disabled("deepseek-legacy-eol")],
            Misses(), retireAfter: 3, maxAdditions: 10);

        Assert.DoesNotContain(diff.Changes, c => c.Kind == RosterChangeKind.Add);
    }

    [Fact]
    public void ComputeDiff_SyncRetiredEntryReappears_ProposesReEnable()
    {
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "deepseek",
            ["deepseek-v4-flash"],
            [Disabled("deepseek-v4-flash", auto: true, retired: true)],
            Misses(), retireAfter: 3, maxAdditions: 10);

        RosterChange reenable = Assert.Single(diff.Changes, c => c.Kind == RosterChangeKind.ReEnable);
        Assert.Equal("deepseek-v4-flash", reenable.Model);
    }

    [Fact]
    public void ComputeDiff_CuratedEntryRetiredBySyncReappears_ProposesReEnable()
    {
        // A curated entry the sync itself retired must be reversible: without the _retired
        // marker it would look like the curator's own enabled:false and stay dead forever.
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "nvidia",
            ["z-ai/glm-5.2"],
            [Disabled("z-ai/glm-5.2", retired: true)],
            Misses(), retireAfter: 3, maxAdditions: 10);

        Assert.Single(diff.Changes, c =>
            c.Kind == RosterChangeKind.ReEnable && c.Model == "z-ai/glm-5.2");
    }

    [Fact]
    public void ComputeDiff_FreshAutoAdditionStillObserved_ProducesNoChange()
    {
        // The addition landed disabled for review and the model is still on the catalog. The
        // next cycle must neither re-add it nor re-enable it — otherwise ROSTER_AUTO_ENABLE=false
        // would be undone by the sync itself one hour later.
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "deepseek",
            ["deepseek-flash"],
            [Disabled("deepseek-flash", auto: true)],
            Misses(), retireAfter: 3, maxAdditions: 10);

        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void ComputeDiff_NonChatId_IsNeverAdded()
    {
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "openai",
            ["text-embedding-ada-002", "omni-moderation-latest"],
            [],
            Misses(), retireAfter: 3, maxAdditions: 10);

        Assert.DoesNotContain(diff.Changes, c => c.Kind == RosterChangeKind.Add);
    }

    [Fact]
    public void ComputeDiff_AdditionsRespectMaxCap()
    {
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "openrouter",
            ["a/one", "b/two", "c/three", "d/four"],
            [],
            Misses(), retireAfter: 3, maxAdditions: 2);

        Assert.Equal(2, diff.Changes.Count(c => c.Kind == RosterChangeKind.Add));
    }

    // ── Retirements ──────────────────────────────────────────────────────────

    [Fact]
    public void ComputeDiff_CuratedEntryBelowThreshold_IsPendingRetire()
    {
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "deepseek",
            ["deepseek-v4-pro"],
            [Enabled("deepseek-v4-pro"), Enabled("deepseek-v4-flash")],
            Misses(("deepseek-v4-flash", 2)), retireAfter: 3, maxAdditions: 10);

        Assert.Contains(diff.Changes, c =>
            c.Kind == RosterChangeKind.PendingRetire && c.Model == "deepseek-v4-flash");
        Assert.DoesNotContain(diff.Changes, c =>
            c.Kind == RosterChangeKind.Retire && c.Model == "deepseek-v4-flash");
    }

    [Fact]
    public void ComputeDiff_CuratedEntryAtThreshold_Retires()
    {
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "deepseek",
            ["deepseek-v4-pro"],
            [Enabled("deepseek-v4-pro"), Enabled("deepseek-v4-flash")],
            Misses(("deepseek-v4-flash", 3)), retireAfter: 3, maxAdditions: 10);

        Assert.Contains(diff.Changes, c =>
            c.Kind == RosterChangeKind.Retire && c.Model == "deepseek-v4-flash");
    }

    [Fact]
    public void ComputeDiff_AutoEntryRetiresOnFirstMiss()
    {
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "deepseek",
            ["deepseek-v4-pro"],
            [Enabled("deepseek-v4-pro"), AutoEnabled("deepseek-v4-flash")],
            Misses(("deepseek-v4-flash", 1)), retireAfter: 3, maxAdditions: 10);

        Assert.Contains(diff.Changes, c =>
            c.Kind == RosterChangeKind.Retire && c.Model == "deepseek-v4-flash");
    }

    [Fact]
    public void ComputeDiff_KeepEnabledEntryNeverProposesRetirement()
    {
        // deepseek-v4-flash: 219 consecutive catalog misses while still answering real requests.
        // The curator marked _keep_enabled after live verification; every later cycle must stop
        // re-proposing the retirement, or ROSTER_MODE=sync would eventually kill a working model.
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "deepseek",
            ["deepseek-v4-pro", "deepseek-flash"],
            [
                Enabled("deepseek-v4-pro"),
                new ModelSelectionEntry("deepseek-v4-flash", 3, true, new(), KeepEnabled: true),
            ],
            Misses(("deepseek-v4-flash", 219)), retireAfter: 3, maxAdditions: 10);

        Assert.DoesNotContain(diff.Changes, c =>
            (c.Kind == RosterChangeKind.Retire || c.Kind == RosterChangeKind.PendingRetire)
            && c.Model == "deepseek-v4-flash");
    }

    // ── Safety: a failed fetch must not retire anything ──────────────────────

    [Fact]
    public void ComputeDiff_EmptyObservation_ProducesNoChanges()
    {
        RosterProviderDiff diff = ModelRosterSyncService.ComputeDiff(
            "deepseek",
            [],
            [Enabled("deepseek-v4-pro")],
            Misses(("deepseek-v4-pro", 9)), retireAfter: 3, maxAdditions: 10);

        Assert.Empty(diff.Changes);
        Assert.NotNull(diff.Note);
    }

    // ── Observe / miss counting ──────────────────────────────────────────────

    [Fact]
    public void Observe_IncrementsMissesForAbsentEnabledEntries()
    {
        Environment.SetEnvironmentVariable("ROSTER_MODE", "observe");
        string dir = MakeTempDir();
        File.WriteAllText(Path.Combine(dir, "deepseek.json"),
            """
            { "provider": "deepseek", "models": [ { "match": "deepseek-v4-pro", "priority": 1, "enabled": true } ] }
            """);

        ModelRosterSyncService roster = BuildSyncService(["deepseek-v4-pro"], configDir: dir);

        roster.Observe("deepseek", ["deepseek-v4-pro"]);
        Assert.Equal(0, roster.GetMissCount("deepseek", "deepseek-v4-pro"));

        // A later pass that no longer lists it counts one miss.
        roster.Observe("deepseek", ["deepseek-v4-flash"]);
        Assert.Equal(1, roster.GetMissCount("deepseek", "deepseek-v4-pro"));
    }

    [Fact]
    public void Observe_IgnoresEmptyDiscoveryList()
    {
        Environment.SetEnvironmentVariable("ROSTER_MODE", "observe");
        string dir = MakeTempDir();
        File.WriteAllText(Path.Combine(dir, "deepseek.json"),
            """
            { "provider": "deepseek", "models": [ { "match": "deepseek-v4-pro", "priority": 1, "enabled": true } ] }
            """);

        ModelRosterSyncService roster = BuildSyncService(["deepseek-v4-pro"], configDir: dir);

        roster.Observe("deepseek", ["deepseek-v4-pro"]);
        roster.Observe("deepseek", []);

        Assert.Equal(0, roster.GetMissCount("deepseek", "deepseek-v4-pro"));
    }

    // ── Integration: a sync cycle writes the config file ─────────────────────

    [Fact]
    public async Task RunCycle_SyncMode_WritesAdditionIntoProviderConfig()
    {
        string dir = MakeTempDir();
        Environment.SetEnvironmentVariable("PROXY_DATA_DIR", dir);
        Environment.SetEnvironmentVariable("ROSTER_MODE", "sync");

        string seeded = Path.Combine(dir, "deepseek.json");
        File.WriteAllText(seeded, """
            { "provider": "deepseek", "models": [ { "match": "deepseek-v4-pro", "priority": 1, "enabled": true } ] }
            """);

        // The provider now also publishes a brand-new id the config does not know.
        (ModelRosterSyncService roster, ModelCatalogService catalog) =
            BuildSyncServiceAndCatalog(["deepseek-v4-pro", "deepseek-v5-turbo"], configDir: dir);
        roster.WriteDirOverrideForTest = dir;

        RosterCycleResult result = await roster.RunCycleAsync(catalog, CancellationToken.None, force: true);

        Assert.NotEmpty(result.FilesWritten);
        JsonNode root = JsonNode.Parse(File.ReadAllText(seeded))!;
        JsonArray models = root["models"]!.AsArray();
        string[] matches = [.. models.OfType<JsonObject>().Select(m => m["match"]!.GetValue<string>())];

        Assert.Contains("deepseek-v5-turbo", matches);   // addition landed
        Assert.Contains("deepseek-v4-pro", matches);      // existing entry preserved
        JsonObject added = models.OfType<JsonObject>().First(m => m["match"]!.GetValue<string>() == "deepseek-v5-turbo");
        Assert.True(added["_auto"]!.GetValue<bool>());
        Assert.False(added["enabled"]!.GetValue<bool>()); // AutoEnable defaults off → disabled for review
    }

    [Fact]
    public async Task RunCycle_ObserveMode_ProposesButWritesNothing()
    {
        string dir = MakeTempDir();
        Environment.SetEnvironmentVariable("PROXY_DATA_DIR", dir);
        Environment.SetEnvironmentVariable("ROSTER_MODE", "observe");

        string seeded = Path.Combine(dir, "deepseek.json");
        string original = """
            { "provider": "deepseek", "models": [ { "match": "deepseek-v4-pro", "priority": 1, "enabled": true } ] }
            """;
        File.WriteAllText(seeded, original);

        (ModelRosterSyncService roster, ModelCatalogService catalog) =
            BuildSyncServiceAndCatalog(["deepseek-v4-pro", "deepseek-v5-turbo"], configDir: dir);
        roster.WriteDirOverrideForTest = dir;

        RosterCycleResult result = await roster.RunCycleAsync(catalog, CancellationToken.None, force: false);

        Assert.Empty(result.FilesWritten);
        Assert.Contains(result.Diffs.SelectMany(d => d.Changes), c =>
            c.Kind == RosterChangeKind.Add && c.Model == "deepseek-v5-turbo");
        Assert.Equal(original, File.ReadAllText(seeded)); // untouched
    }

    [Fact]
    public async Task RunCycle_SyncMode_PreservesUnicodePunctuationAndMergesDuplicateComments()
    {
        // Two writer bugs that would corrupt hand-curated files, both found on the first live apply:
        // (1) the default STJ encoder escapes every em-dash in a curator's _comment to \u2014;
        // (2) JsonNode throws on a duplicated _comment key - a real shape in groq.json - and the
        // whole provider's renewal is lost. The writer must keep prose readable and tolerate the
        // duplicate by merging the two comments into one.
        string dir = MakeTempDir();
        Environment.SetEnvironmentVariable("PROXY_DATA_DIR", dir);
        Environment.SetEnvironmentVariable("ROSTER_MODE", "sync");

        // The seeded file must declare the provider the cycle renews (deepseek, per the stub
        // registry below), otherwise the writer creates a new file and never round-trips this one.
        string seeded = Path.Combine(dir, "deepseek.json");
        File.WriteAllText(seeded, """
            { "provider": "deepseek", "models": [
              { "_comment": "first — curated note.",
                "_comment": "second — another note.",
                "match": "deepseek-v4-pro", "priority": 1, "enabled": true } ] }
            """);

        (ModelRosterSyncService roster, ModelCatalogService catalog) =
            BuildSyncServiceAndCatalog(["deepseek-v4-pro", "deepseek-flash"], configDir: dir);
        roster.WriteDirOverrideForTest = dir;

        RosterCycleResult result = await roster.RunCycleAsync(catalog, CancellationToken.None, force: true);

        Assert.NotEmpty(result.FilesWritten);
        string written = File.ReadAllText(seeded);
        Assert.Contains("—", written);                       // em-dashes stay literal
        Assert.DoesNotContain("\\u2014", written);           // never escaped
        Assert.Contains("first — curated note. second — another note.", written); // merged
        Assert.Contains("deepseek-flash", written);          // renewal still landed
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string MakeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "roster-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // A store loaded from a throwaway directory (empty when none given), so the curated
    // workspace roster can never leak into these tests' expectations.
    private static ModelSelectionStore BuildStore(string? configDir) =>
        new(configDir ?? Path.Combine(Path.GetTempPath(), "roster-empty-" + Guid.NewGuid().ToString("n")));

    // Builds a deepseek-only provider registry whose /v1/models answers with the given ids.
    private static ProviderRegistry BuildRegistry(string[] deepseekModels)
    {
        FakeProviderHandler handler = new(new Dictionary<string, string[]> { ["deepseek"] = deepseekModels });
        ProviderHttpClientFactory factory = new(handler);

        foreach (string provName in ProviderCapabilitiesRegistry.KnownProviders)
        {
            ProviderCapabilities caps = ProviderCapabilitiesRegistry.Get(provName);
            Environment.SetEnvironmentVariable($"PROVIDER_{caps.EnvPrefix}_API_KEY", null);
            Environment.SetEnvironmentVariable($"PROVIDER_{caps.EnvPrefix}_BASE_URL", null);
        }
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_API_KEY", "test-key");
        Environment.SetEnvironmentVariable("PROVIDER_DEEPSEEK_BASE_URL", "http://deepseek.test/");
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);

        return new ProviderRegistry(factory);
    }

    private static ModelRosterSyncService BuildSyncService(string[] deepseekModels, string? configDir)
    {
        ProviderRegistry registry = BuildRegistry(deepseekModels);
        return new ModelRosterSyncService(registry, BuildStore(configDir));
    }

    private static (ModelRosterSyncService, ModelCatalogService) BuildSyncServiceAndCatalog(string[] deepseekModels, string? configDir)
    {
        ProviderRegistry registry = BuildRegistry(deepseekModels);
        ModelSelectionStore store = BuildStore(configDir);
        ModelCatalogService catalog = new(registry, store);
        ModelRosterSyncService roster = new(registry, store);
        return (roster, catalog);
    }
}
