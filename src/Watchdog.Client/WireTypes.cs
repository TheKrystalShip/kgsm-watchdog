using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// Result of a control action (<c>start</c>/<c>stop</c>): which instance, success, and a reason.
/// </summary>
/// <param name="Instance">The instance the action was asked of.</param>
/// <param name="Ok">Whether the action was carried out.</param>
/// <param name="Message">The reason, in the shape a person reads.</param>
/// <param name="Refusal">
/// Which kind of refusal this is, when it is one — a value from <see cref="ActionRefusal"/>, absent
/// otherwise. <see cref="Message"/> is prose meant for a person and is free to be reworded; this is
/// what a caller branches on, so a consumer never has to match on a sentence.
/// </param>
public sealed record ActionResult(
    string Instance,
    bool Ok,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Refusal = null);

/// <summary>
/// The refusal kinds <see cref="ActionResult.Refusal"/> can carry.
/// </summary>
/// <remarks>
/// A refusal is not a failure. Nothing is wrong with the instance and nothing was attempted, so a
/// caller retrying it unchanged gets the same answer — which is why it is worth telling apart from a
/// start that broke. Each kind also has its own HTTP status on the control surface, because a bash
/// caller reads the status line and nothing else.
/// </remarks>
public static class ActionRefusal
{
    /// <summary>The node has no room: what this instance needs, plus what is already committed to the
    /// instances currently starting, would leave the node under the headroom floor.</summary>
    public const string NoRoom = "no_room";

    /// <summary>The instance is held by an instance transfer: its config carries a non-empty
    /// <c>transfer_lock</c>, so it stays stopped until the lock is released.</summary>
    public const string InstanceLocked = "instance_locked";
}


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
    string Reason,        // last transition reason (e.g. "crashed (exit 139); restart in 2s")
    // When the CURRENT run was spawned, UTC, or null when nothing is running and when the daemon
    // adopted a live cgroup it did not spawn. Persisted alongside the phase, so it survives a daemon
    // restart rather than resetting to the moment the daemon came back.
    DateTime? SpawnedAt = null,
    // When the LAST run ended, UTC, or null when no run of this instance has been recorded. Read from
    // the run ledger, so it survives a daemon restart and is not lost when the instance stops being
    // tracked. This is the console file's mtime — the run's own last output — not the moment the
    // supervisor noticed the cgroup had emptied.
    DateTime? LastExitedAt = null);

/// <summary>
/// One instance's run clock: when its current run was spawned, and when its last run ended.
/// </summary>
/// <remarks>
/// Reported for every instance the daemon can date — the ones it is supervising right now AND the ones
/// only the run ledger remembers. That union is the whole point of this being its own record rather than
/// two fields on <see cref="InstanceState"/>: an instance stops being tracked when it stops, so a
/// consumer asking <c>/list</c> how long a stopped server has been down would never be told about the
/// instances that are actually down.
/// </remarks>
/// <param name="Name">The instance name.</param>
/// <param name="SpawnedAt">When the current run was spawned (UTC); null when nothing is running.</param>
/// <param name="LastExitedAt">When the last recorded run ended (UTC); null when no run is on record.</param>
public sealed record InstanceRunTimes(
    string Name,
    DateTime? SpawnedAt,
    DateTime? LastExitedAt);

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

/// <summary>One router port-forward row (<c>GET /upnp/{name}</c>).</summary>
public sealed record UpnpMapping(
    int ExternalPort,
    string Protocol,        // "tcp" | "udp"
    int InternalPort,
    string InternalClient,
    string Description);

/// <summary>An instance's router port-forwards and the honest state of the query.</summary>
/// <param name="State">"queried", or "unavailable" when the router could not be asked; an unreachable router is never an empty list.</param>
public sealed record UpnpListResult(
    string Instance,
    string State,
    IReadOnlyList<UpnpMapping> Mappings);

/// <summary>One player session in the live session map.</summary>
public sealed record PlayerSession(
    string? SessionKey,
    string? Id,
    string? Name,
    string? Addr);

/// <summary>
/// What the daemon can observe of one instance's players: how it detects them and who is on. The two travel
/// together so a caller cannot read an empty list without knowing whether the game can report players.
/// </summary>
/// <param name="Detection">"log", "rcon", "none" or "unknown".</param>
public sealed record InstancePresence(
    string Detection,
    PlayerSession[] Players)
{
    /// <summary>Whether the daemon observes this instance's players at all.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsDetected => Detection is "log" or "rcon";
}

/// <summary>One run of an instance's console (<c>GET /console/{name}/runs</c>), newest first.</summary>
public sealed record ConsoleRun(
    int Index,
    bool Current,
    DateTime? EndedAt,
    DateTime LastOutputAt,
    long SizeBytes,
    string Outcome,
    int? ExitCode);

/// <summary>The memory admission gate's settings as the daemon runs them (<c>GET /memory-gate</c>).</summary>
/// <param name="Enabled">Whether a start is checked against the memory still available.</param>
/// <param name="HeadroomMb">The memory a start must leave free, in MB.</param>
public sealed record MemoryGateState(bool Enabled, int HeadroomMb);
