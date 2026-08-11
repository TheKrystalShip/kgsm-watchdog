using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>Result of a control action (<c>start</c>/<c>stop</c>): which instance, success, and a reason.</summary>
public sealed record ActionResult(string Instance, bool Ok, string Message);

/// <summary>
/// One UPnP port-mapping row read from the local IGD (<c>upnpc -l</c>), owned by an instance — its
/// mapping <c>description</c> equals the instance name (the tag the watchdog sets with <c>-e &lt;name&gt;</c>
/// on open). Measured from the router, never fabricated.
/// </summary>
public sealed record UpnpMapping(
    int ExternalPort,
    string Protocol,        // "tcp" | "udp"
    int InternalPort,
    string InternalClient,
    string Description);

/// <summary>
/// An instance's current UPnP mappings on the IGD. <see cref="State"/> is load-bearing for honesty:
/// <c>"queried"</c> means the router was asked and these are the mappings it owns (possibly an empty
/// list — a real "none"); <c>"unavailable"</c> means the router could not be asked at all (<c>upnpc</c>
/// missing, no IGD on the network, or a timeout) — that is NEVER presented as "no mappings".
/// </summary>
public sealed record UpnpListResult(
    string Instance,
    string State,           // "queried" | "unavailable"
    IReadOnlyList<UpnpMapping> Mappings);

/// <summary>
/// The IGD's whole redirection table, read once for a reconcile sweep. <see cref="Reached"/> carries the
/// same honesty as <see cref="UpnpListResult.State"/>: false means the router could not be asked, which
/// is NOT the same as an empty table and must never be acted on as "nothing is mapped" — a router outage
/// would otherwise read as every forward having expired at once.
/// <para>
/// <see cref="LocalAddress"/> is this host's LAN address as the IGD itself reports it on the same
/// listing. It is what makes a row's <em>target</em> checkable: a mapping pointing at this host on the
/// port it forwards is the mapping this daemon would open, whichever instance's tag it carries. Null
/// when the listing did not report one, and a null must fall back to matching on the tag alone rather
/// than assuming an address.
/// </para>
/// </summary>
internal sealed record UpnpTable(
    bool Reached, IReadOnlyList<UpnpMapping> Mappings, string? LocalAddress = null);

/// <summary>
/// A running instance the UPnP reconcile sweep is answerable for, paired with the spawn config the
/// sweep reads its ports and forwarding gate from.
/// </summary>
public sealed record ForwardingCandidate(string Name, Instance Spec);

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
/// A single player session tracked by the watchdog's <c>PlayerSessionStore</c>. Served by
/// <c>GET /players</c> so consumers (kgsm-api) can reconcile their roster on startup.
/// </summary>
public sealed record PlayerSession(
    string? SessionKey,
    string? Id,
    string? Name,
    string? Addr);

/// <summary>
/// One instance's player presence: whether this daemon can observe it, and who it currently sees.
/// </summary>
/// <remarks>
/// <b><see cref="Detection"/> is what makes <see cref="Players"/> readable.</b> An empty list under
/// <c>log</c> or <c>rcon</c> means nobody is connected — a measured fact. An empty list under
/// <c>none</c> means this game reports nothing, and rendering it as "0 online" states something the
/// host does not know. The two are one record so a consumer cannot take the list without the
/// qualifier.
/// </remarks>
/// <param name="Detection">
/// <c>log</c> (matched from the game's output — real transitions), <c>rcon</c> (polled and diffed —
/// cannot see churn between polls), <c>none</c> (not observable), or <c>unknown</c> (the instance
/// inventory could not be read, so the capability could not be established either way).
/// </param>
/// <param name="Players">The sessions currently tracked. Empty is meaningful only alongside
/// <paramref name="Detection"/>.</param>
public sealed record InstancePresence(
    string Detection,
    PlayerSession[] Players);

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

/// <summary>
/// One run of a native instance's console — a single stretch of stdout between a spawn and the exit
/// that followed it. <see cref="SpawnEngine"/> rotates the log on every fresh spawn, so a run's
/// output is one file: the live <c>Instance.LogFile</c> for the run in progress, a rotated sibling in
/// the instance's logs directory for every run before it.
/// <para>
/// <b><see cref="Index"/> is the handle.</b> 0 is the current run, 1 the one before it, and so on —
/// newest first. It is the only address a client gets; the file path stays inside the daemon so a
/// consumer cannot be tempted to read around the instance boundary.
/// </para>
/// <para>
/// <b>Ordering and <see cref="EndedAt"/> come from each file's last-write time, never its name.</b>
/// The last write IS the last line the run printed, which makes it the measured end of the run,
/// while a name is only ever a label — and a log rotated by an older build carries a name stamped
/// with the rotation moment instead, which for an instance that sat stopped is days off. Reading the
/// filesystem rather than the label is what keeps those files ordered correctly.
/// </para>
/// </summary>
/// <param name="Index">Newest-first position; 0 is the most recent run.</param>
/// <param name="Current">
/// Whether this run is IN PROGRESS — a process alive in the instance's cgroup, writing this file
/// right now. Holding the live log is not enough: an instance that stopped, or that crashed with no
/// restart behind it, keeps its last log at that path until the next spawn rotates it, and that run
/// is over. At most one run is current, and a stopped instance has none.
/// </param>
/// <param name="EndedAt">
/// When the run's output stopped, UTC. Null only while <see cref="Current"/> — a run in progress has
/// no end, and a timestamp there would claim otherwise. Every finished run has one, including the
/// unrotated last run of a stopped instance, which is what lets a caller correlate a crash against
/// the run that ended at it even when nothing restarted afterwards.
/// </param>
/// <param name="LastOutputAt">When the run last printed, UTC. Always measured, for every run.</param>
/// <param name="SizeBytes">The run's console size on disk.</param>
public sealed record ConsoleRun(
    int Index,
    bool Current,
    DateTime? EndedAt,
    DateTime LastOutputAt,
    long SizeBytes);
