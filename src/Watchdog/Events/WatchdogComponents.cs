namespace TheKrystalShip.KGSM.Watchdog.Events;

/// <summary>
/// The parts of this daemon's job that can stop working while it keeps running.
/// </summary>
/// <remarks>
/// <para>
/// Each one is a <c>leaf_degraded</c> component. They are held together here rather than as strings at
/// the call sites because the id is the dedup key: two spellings of one component would report the same
/// fault twice and recover from neither.
/// </para>
/// <para>
/// <b>Bounded, and deliberately not per-instance.</b> A component named after the instance it was
/// observed on would grow without limit and never recover. These name the capability; the instance goes
/// in the detail.
/// </para>
/// </remarks>
internal static class WatchdogComponents
{
    /// <summary>
    /// systemd's delegation of a writable cgroup subtree — what makes this daemon able to spawn at all.
    /// </summary>
    /// <remarks>
    /// The same answer <c>/health</c> serves. Degraded here means <c>/start</c> is refusing with a
    /// reason, so it is the daemon being unable to do its whole job rather than part of it.
    /// </remarks>
    public const string Delegation = "cgroup-delegation";

    /// <summary>
    /// The cgroup controllers per-instance children inherit from the delegated base.
    /// </summary>
    /// <remarks>
    /// The daemon still spawns without them; what stops is accounting. A game runs, and its memory reads
    /// as zero — which looks like a monitoring fault and is a supervision one.
    /// </remarks>
    public const string Controllers = "cgroup-controllers";

    /// <summary>
    /// <c>cgroup.kill</c>, the atomic whole-subtree kill.
    /// </summary>
    /// <remarks>
    /// Without it a stop cannot guarantee it took the whole process tree with it, so a game that
    /// forked leaves survivors holding the port. A kernel gains no features while running, so this
    /// degradation is for the life of the process and recovers only across a reboot.
    /// </remarks>
    public const string CgroupKill = "cgroup-kill";
}
