namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// xUnit serializes tests in the same collection (no intra-collection parallelism). Several test
/// classes mutate <b>process-global</b> environment variables (<c>HOME</c>, <c>XDG_DATA_HOME</c>,
/// <c>KGSM_WATCHDOG_*</c>, <c>SUDO_*</c>) and restore them in a finally/Dispose. Run in parallel, one
/// class's set could be observed by another mid-read — a flaky failure unrelated to the code under
/// test. Putting every env-mutating class in this one collection makes those mutations sequential, so
/// the save/restore windows never overlap.
/// </summary>
[CollectionDefinition(Name)]
public sealed class EnvironmentCollection
{
    public const string Name = "process-global-environment";
}
