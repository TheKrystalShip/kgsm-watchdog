namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// The on-disk shape of the daemon's per-instance <b>supervision counters</b> (written by
/// <c>SupervisionStateStore</c>, a companion to <c>desired-state.json</c>). Unlike the boot-autostart
/// set — which is pure operator intent — this captures the <em>runtime</em> restart bookkeeping
/// (<see cref="InstanceRestartState.ConsecutiveFailures"/> / <see cref="InstanceRestartState.GaveUp"/>)
/// plus the phase + timing, so they survive <b>any</b> daemon death — a planned hot-swap, but also an
/// unclean SIGKILL/OOM. Without it, an OOM of the daemon silently resets a crash-looping instance's
/// failure streak to zero, fabricating health the operator never earned — exactly the kind of invented
/// state the ecosystem forbids.
/// <para>
/// <see cref="Version"/> is carried for forward-compat (same convention as
/// <see cref="PersistedDesiredState"/>), so a future shape change can migrate rather than misread.
/// </para>
/// </summary>
internal sealed class PersistedSupervisionState
{
    public int Version { get; set; } = 1;

    /// <summary>Keyed by instance name → its last-known restart bookkeeping.</summary>
    public Dictionary<string, InstanceRestartState> Instances { get; set; } = new();
}

/// <summary>
/// One instance's persisted supervision counters. <see cref="Phase"/> is stored as a STRING (the
/// <c>SupervisionPhase</c> enum name) rather than an int: it stays human-readable on disk and is
/// forward-compatible (an unknown name parses back to a no-op via <c>Enum.TryParse</c> instead of
/// silently aliasing the wrong member).
/// </summary>
internal sealed class InstanceRestartState
{
    public int ConsecutiveFailures { get; set; }
    public bool GaveUp { get; set; }
    public string Phase { get; set; } = "";
    public DateTime? SpawnedAt { get; set; }
    public DateTime? RunStartedAt { get; set; }
    public DateTime? NextRestartAt { get; set; }
    public DateTime? MaintenanceSince { get; set; }
    public string LastReason { get; set; } = "";
}
