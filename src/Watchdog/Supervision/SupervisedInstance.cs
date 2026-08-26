using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>The supervision lifecycle of one instance, as the daemon sees it.</summary>
internal enum SupervisionPhase
{
    /// <summary>A live process is (or should be) in the cgroup; the reconcile loop watches it for exit.</summary>
    Running,

    /// <summary>Crashed and waiting out the backoff delay; will respawn at <see cref="SupervisedInstance.NextRestartAt"/>.</summary>
    RestartPending,

    /// <summary>Deliberately stopped, or exited cleanly (exit 0) — terminal until a manual start.</summary>
    Stopped,

    /// <summary>Crash-looped past the retry limit — terminal until a manual start clears the give-up latch.</summary>
    Failed,

    /// <summary>
    /// Parked: deliberately drained so a leaf can do disruptive work against a stopped server, while
    /// <see cref="SupervisedInstance.DesiredRunning"/> stays true. Crash-restart is suppressed for as
    /// long as the park holds, and the failure streak and give-up latch are left exactly as they were —
    /// a park is not a crash and not a stop, so it neither punishes the instance nor forgives it.
    /// Released by <c>maintenance/end</c>, by an operator's start, or by the daemon's own unpark
    /// timeout.
    /// </summary>
    Maintenance,
}

/// <summary>
/// The daemon's durable record for one instance. Unlike <see cref="RunningInstance"/> (which exists
/// only while the game is up), this <b>persists across respawns and the down-window</b> — it is where
/// desired-state, the restart counter, and the phase live, none of which may vanish when the process
/// dies. Keyed by instance name in the supervisor; mutated only under the supervisor's gate.
/// </summary>
internal sealed class SupervisedInstance
{
    public required string Name { get; init; }

    /// <summary>The spawn config captured at start, reused verbatim on every restart (no mid-supervision config drift).</summary>
    public required Instance Spec { get; set; }

    /// <summary>Operator intent: was the daemon last told to run this? A deliberate <c>stop</c> sets it false.</summary>
    public bool DesiredRunning { get; set; } = true;

    /// <summary>The live process handle while <see cref="Phase"/> is <see cref="SupervisionPhase.Running"/>; otherwise null.</summary>
    public RunningInstance? Current { get; set; }

    public SupervisionPhase Phase { get; set; } = SupervisionPhase.Running;

    /// <summary>Backoff/give-up state; survives respawns by living here, not on <see cref="Current"/>.</summary>
    public RestartTracker Restart { get; } = new();

    /// <summary>When <see cref="Current"/> was last spawned — drives the grace window and stability reset.</summary>
    public DateTime? SpawnedAt { get; set; }

    /// <summary>When a <see cref="SupervisionPhase.RestartPending"/> instance becomes due to respawn.</summary>
    public DateTime? NextRestartAt { get; set; }

    /// <summary>
    /// When the current park began, or null when the instance is not parked. The unpark timeout is
    /// measured from here, which is what keeps a leaf that dies mid-sequence from costing a server
    /// rather than a window.
    /// </summary>
    public DateTime? MaintenanceSince { get; set; }

    /// <summary>Human-readable last transition reason, surfaced on <c>/status</c>.</summary>
    public string LastReason { get; set; } = "";

    public string DesiredText => DesiredRunning ? "running" : "stopped";

    public string PhaseText => Phase switch
    {
        SupervisionPhase.Running => "running",
        SupervisionPhase.RestartPending => "restart-pending",
        SupervisionPhase.Stopped => "stopped",
        SupervisionPhase.Failed => "failed",
        SupervisionPhase.Maintenance => "maintenance",
        _ => "unknown",
    };
}
