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
/// Outcome of an on-demand UPnP open/close. <see cref="Outcome"/> is <c>"applied"</c> (the IGD confirmed
/// the change — the only path that emits an audit event), <c>"skipped"</c> (port-forwarding disabled for
/// the instance, or it has no ports — nothing changed), or <c>"failed"</c> (<c>upnpc</c> could not
/// deliver: missing binary, non-zero on open, or timeout). A skipped or failed open is never reported as
/// an open.
/// </summary>
public sealed record UpnpActionResult(
    string Instance,
    string Outcome,         // "applied" | "skipped" | "failed"
    string Detail);

/// <summary>
/// Optional request body for <c>POST /upnp/{name}/open</c>: an explicit port set to forward instead of
/// the instance's configured ports (parity with the firewall's <c>ensure-open &lt;instance&gt; &lt;ports&gt;</c>).
/// Absent or empty → the instance's own configured ports.
/// </summary>
public sealed record UpnpOpenRequest(List<PortMapping>? Ports);

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
