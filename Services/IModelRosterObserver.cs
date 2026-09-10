/// <summary>
/// Receives the raw model ids a provider publishes on each discovery pass, so a consumer
/// (the roster sync) can propose additions and retirements without the catalog depending on it.
/// </summary>
internal interface IModelRosterObserver
{
    void Observe(string providerName, string[] discovered);
}
