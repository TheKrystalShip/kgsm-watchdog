namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// The on-disk shape of <b>which run of each instance has already been announced ready</b> (written by
/// <c>ReadinessStateStore</c>, a companion to <c>supervision-state.json</c>).
/// <para>
/// <c>instance-ready</c> says a server finished booting and is joinable — a transition, so it belongs to
/// a run and happens once in it. The daemon detects that transition from its own first observation of a
/// populated cgroup, and that observation is not the same thing as the transition: an instance that was
/// already running when this daemon started produces one every single time the daemon starts, for a run
/// that never stopped. The latch that prevents a second announcement therefore has to outlive the
/// process holding it, which is what this file is.
/// </para>
/// <para>
/// <see cref="Version"/> is carried for forward-compat, the same convention as
/// <see cref="PersistedSupervisionState"/>.
/// </para>
/// </summary>
internal sealed class PersistedReadinessState
{
    public int Version { get; set; } = 1;

    /// <summary>Keyed by instance name → the run its readiness was last announced for.</summary>
    public Dictionary<string, InstanceReadinessState> Instances { get; set; } = new();
}

/// <summary>One instance's last readiness announcement.</summary>
internal sealed class InstanceReadinessState
{
    /// <summary>
    /// The run that was announced, named by <c>ProcessStartClock.RunKeyFor</c> — the leader's pid and
    /// the kernel tick it started on. Comparing it against the run occupying the cgroup right now is
    /// what separates "this daemon has only just noticed a server that has been up for days" from "this
    /// server has genuinely just started".
    /// </summary>
    public string RunKey { get; set; } = "";

    /// <summary>When the announcement went out, for an operator reading the file.</summary>
    public DateTime AnnouncedAt { get; set; }
}
