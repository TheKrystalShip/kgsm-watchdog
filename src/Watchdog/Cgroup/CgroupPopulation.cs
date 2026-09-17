namespace TheKrystalShip.KGSM.Watchdog.Cgroup;

/// <summary>
/// What one read of an instance cgroup's <c>populated</c> state found.
/// </summary>
/// <remarks>
/// Three values because "not populated" and "could not read" lead to opposite actions: the first ends a
/// run, the second must not.
/// </remarks>
internal enum CgroupPopulation
{
    /// <summary>The cgroup is absent, or reports no live process.</summary>
    Empty,

    /// <summary>The cgroup reports at least one live process.</summary>
    Populated,

    /// <summary>The state could not be read.</summary>
    Unknown,
}
