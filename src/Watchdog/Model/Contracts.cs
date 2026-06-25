namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>Result of a control action (<c>start</c>/<c>stop</c>): which instance, success, and a reason.</summary>
public sealed record ActionResult(string Instance, bool Ok, string Message);

/// <summary>
/// Reported state of a supervised instance: the <em>desired</em> run-state and the boot-autostart
/// <em>enabled</em> flag the daemon holds vs. the <em>actual</em> liveness measured from
/// <c>cgroup.events</c>, plus the supervision phase and the current restart-failure streak. Never
/// fabricated — <c>populated</c> is read from the kernel, and an instance the daemon does not track
/// simply does not appear. <c>enabled</c> and <c>desired</c> are independent axes (systemctl-style):
/// <c>desired</c> is the runtime intent set by start/stop, <c>enabled</c> is the persisted
/// boot-autostart intent set by enable/disable.
/// </summary>
public sealed record InstanceState(
    string Name,
    string Desired,       // "running" | "stopped" — runtime intent (start/stop)
    bool Enabled,         // in the persisted boot-autostart set (enable/disable)
    bool Populated,       // measured from cgroup.events
    int? Pid,             // the spawned leader PID, when known
    string CgroupPath,
    string Phase,         // "running" | "restart-pending" | "stopped" | "failed" | "unknown"
    int Restarts,         // consecutive-failure streak since last stability (0 when healthy)
    string Reason);       // last transition reason (e.g. "crashed (exit 139); restart in 2s")

/// <summary>Readiness of the supervisor itself: whether it is in-slice and able to spawn.</summary>
public sealed record ReadyState(bool Ready, string Detail);

/// <summary>
/// The running daemon's build identity (Inc 7 Phase 0): the assembly informational version split
/// into its semantic <c>version</c> and the source-control <c>commit</c> hash (the <c>+&lt;hash&gt;</c>
/// suffix SourceLink stamps on the informational version). Served by <c>GET /version</c> so the
/// hot-swap deploy can confirm the post-swap build directly — never fabricated; both fields come from
/// the compiled-in <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>.
/// </summary>
public sealed record WatchdogVersionInfo(
    [property: System.Text.Json.Serialization.JsonPropertyName("version")] string Version,
    [property: System.Text.Json.Serialization.JsonPropertyName("commit")] string Commit)
{
    /// <summary>
    /// Pure, side-effect-free parse of an informational-version string into its semantic version and
    /// (optional) commit hash. SourceLink stamps the informational version as
    /// <c>&lt;version&gt;+&lt;commit-hash&gt;</c>; everything before the first <c>'+'</c> is the
    /// version, everything after is the commit. No <c>'+'</c> → the whole string is the version and
    /// the commit is empty. A null/empty input yields version <c>"0.0.0"</c> with no commit so the
    /// surface never returns a null field. Kept static + pure so it is unit-testable apart from the
    /// reflection that reads the attribute.
    /// </summary>
    public static WatchdogVersionInfo FromInformational(string? informational)
    {
        if (string.IsNullOrEmpty(informational))
            return new WatchdogVersionInfo("0.0.0", "");

        var plus = informational.IndexOf('+');
        return plus < 0
            ? new WatchdogVersionInfo(informational, "")
            : new WatchdogVersionInfo(informational[..plus], informational[(plus + 1)..]);
    }
}
