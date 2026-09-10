/// <param name="AutoManaged">
/// Set from the <c>_auto</c> flag in config/model-selection/*.json. True for entries the roster
/// sync generated itself; those retire on the first observed absence, while curated entries
/// need <c>ROSTER_RETIRE_AFTER</c> consecutive misses before anything touches them.
/// </param>
public record struct ModelSelectionEntry(string Match, int Priority, bool Enabled, ModelExecutionConfig Execution, string? Upstream = null, bool AutoManaged = false);
