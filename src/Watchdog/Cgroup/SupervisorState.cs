namespace TheKrystalShip.KGSM.Watchdog.Cgroup;

/// <summary>
/// Shared, mutable flag describing whether the daemon successfully placed itself inside the
/// delegated <c>kgsm.slice/&lt;supervisor&gt;</c> cgroup at boot. Spawning requires this: a daemon
/// that is NOT in the slice cannot perform the intra-slice move that lands a game in its own
/// cgroup (cgroup v2 delegation containment). The control surface reports "not ready" with the
/// reason rather than silently failing a <c>start</c>.
/// </summary>
internal sealed class SupervisorState
{
    private volatile bool _ready;
    private volatile string _detail = "bootstrap not run";

    /// <summary>True once the daemon is in-slice and may spawn instances.</summary>
    public bool Ready
    {
        get => _ready;
        set => _ready = value;
    }

    /// <summary>Human-readable explanation of the current readiness (the boot path taken, or why it failed).</summary>
    public string Detail
    {
        get => _detail;
        set => _detail = value;
    }
}
