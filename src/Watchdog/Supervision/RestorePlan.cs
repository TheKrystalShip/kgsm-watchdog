using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>What the daemon should do with one persisted desired-running instance at startup.</summary>
internal enum RestoreAction
{
    /// <summary>
    /// Its cgroup is already populated — a process that outlived a daemon restart. Re-attach
    /// supervision WITHOUT killing/respawning it (a live game must survive a daemon bounce).
    /// </summary>
    Adopt,

    /// <summary>Its cgroup is empty — the process is gone (host reboot / clean daemon stop). Spawn it fresh.</summary>
    Spawn,

    /// <summary>
    /// kgsm-lib returned no config: the instance was removed since it was enabled, OR a transient read
    /// failure. Skip it this boot, but keep the persisted intent (only an explicit stop prunes it).
    /// </summary>
    SkipGone,

    /// <summary>No longer a native standalone instance (became container/systemd) — outside the watchdog's remit.</summary>
    SkipOutOfScope,
}

/// <summary>
/// The startup restore decision, factored out as a pure function. This three-way fork (adopt a live
/// cgroup vs. spawn a dead one vs. skip) is the riskiest new logic in the persistence increment and is
/// otherwise only reachable through the root validation script — isolating it from the cgroup/fork I/O
/// makes it exhaustively unit-testable, the same split as <c>BackoffPolicy</c>.
/// </summary>
internal static class RestorePlan
{
    /// <param name="cgroupPopulated">Whether the instance's cgroup currently holds a live process.</param>
    /// <param name="instance">The spec kgsm-lib returned, or null if it had no config for this name.</param>
    public static RestoreAction Classify(bool cgroupPopulated, Instance? instance)
    {
        if (instance is null)
            return RestoreAction.SkipGone;

        if (instance.Runtime != InstanceRuntime.Native || instance.LifecycleManager != LifecycleManager.Standalone)
            return RestoreAction.SkipOutOfScope;

        return cgroupPopulated ? RestoreAction.Adopt : RestoreAction.Spawn;
    }
}
