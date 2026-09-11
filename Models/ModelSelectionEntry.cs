/// <param name="AutoManaged">
/// Set from the <c>_auto</c> flag in config/model-selection/*.json. True for entries the roster
/// sync generated itself; those retire on the first observed absence, while curated entries
/// need <c>ROSTER_RETIRE_AFTER</c> consecutive misses before anything touches them.
/// </param>
/// <param name="Retired">
/// Set from <c>_retired</c>, written only by the roster sync when it retires an entry. It marks
/// "this disable is the sync's, and reversible": if the model reappears on the catalog the sync
/// re-enables it. An entry that is disabled without this flag is either a curator's deliberate
/// <c>enabled:false</c> or a fresh auto-addition awaiting review — the sync leaves both alone,
/// which is what keeps ROSTER_AUTO_ENABLE=false from silently self-enabling on the next cycle.
/// </param>
/// <param name="KeepEnabled">
/// Set from <c>_keep_enabled</c>. Exempts an enabled entry from catalog-absence retirement. The
/// first live run found the false positive: DeepSeek stopped listing <c>deepseek-v4-flash</c> in
/// <c>/v1/models</c> while the id still answers real requests (catalog ≠ callable, the Cerebras
/// lesson inverted). A kept entry is never proposed for retirement; the curator's <c>_comment</c>
/// should record the live verification that justified it.
/// </param>
public record struct ModelSelectionEntry(string Match, int Priority, bool Enabled, ModelExecutionConfig Execution, string? Upstream = null, bool AutoManaged = false, bool Retired = false, bool KeepEnabled = false);
