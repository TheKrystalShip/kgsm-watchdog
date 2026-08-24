using System.Reflection;
using System.Text.Json;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Control;

/// <summary>
/// The control protocol (PLAN §5), served as HTTP/1.1 over the unix socket exactly like the
/// monitor's <c>/metrics</c> — same transport, same source-gen JSON, so kgsm-lib gets a tiny typed
/// client and the bot/CLI reuse it. The socket's filesystem perms are the security boundary; there
/// is no in-daemon authn (that belongs to the network-facing surfaces above).
/// </summary>
internal static class ControlEndpoints
{
    /// <summary>
    /// The status line for one action result: <c>200</c> acted, <c>507</c> the node has no room,
    /// <c>409</c> everything else.
    /// </summary>
    /// <remarks>
    /// <c>507 Insufficient Storage</c> is the apt one — the request is well-formed and the instance is
    /// fine; the host cannot hold what it asks for right now. It is a distinct code rather than a field
    /// alone because the kgsm CLI's transport reads the status and discards the body, so a discriminator
    /// that lived only in the JSON would not reach the caller that most needs it.
    /// </remarks>
    internal static int StatusFor(ActionResult result) => result switch
    {
        { Ok: true } => StatusCodes.Status200OK,
        { Refusal: ActionRefusal.NoRoom } => StatusCodes.Status507InsufficientStorage,
        _ => StatusCodes.Status409Conflict,
    };

    /// <summary>
    /// Whether a query flag reads as set. Absent, empty, and anything unrecognised are all false.
    /// </summary>
    /// <remarks>
    /// Parsed rather than bound as a <c>bool?</c> so a spelling the binder rejects (<c>?force=1</c>) is
    /// a start without the override rather than a <c>400</c> on the request. Unset is the protected
    /// direction, which is what a value nobody can read should mean.
    /// </remarks>
    internal static bool IsTrue(string? flag) =>
        flag is "1" or "true" or "TRUE" or "True" or "yes" or "on";

    public static void MapWatchdog(this WebApplication app)
    {
        // Unified ecosystem health probe — one `/health` everywhere (PLAN §5). This carries
        // *readiness* semantics, not bare liveness: 200 only when the supervisor is in-slice and
        // able to spawn; 503 + reason when up-but-unable; no answer at all when down. Consumers
        // treat anything other than 200 as "unavailable — retry until 200".
        IResult Health(SupervisorState state)
        {
            var body = new ReadyState(state.Ready, state.Detail);
            return state.Ready
                ? Results.Json(body, WatchdogJsonContext.Default.ReadyState)
                : Results.Json(body, WatchdogJsonContext.Default.ReadyState, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        app.MapGet("/health", Health);
        app.MapGet("/ready", Health); // /ready serves the same health payload as /health

        // 200 acted (including a genuine idempotent no-op — "already running"), 507 the node has no room,
        // 409 anything else that went wrong. The three are separated on the STATUS LINE because the kgsm
        // CLI reads the code and nothing else, and a capacity refusal has to reach it as its own exit
        // code: nothing is wrong with the instance, so a caller must not retry it as a failure or report
        // it as a fault. The body carries the same answer as ActionResult.Refusal for callers that read
        // JSON — neither side is a sentence to be matched.
        //
        // `?force=true` carries a person's override of the capacity check, and a query parameter is what
        // makes it reachable: the CLI's transport builds a URL and reads the status back, with no body in
        // either direction. It is a QUERY rather than a header or a body field for the same reason the
        // status code carries the refusal — the caller that needs it is a curl invocation.
        app.MapPost("/start/{name}", async (string name, string? force, InstanceSupervisor sup, CancellationToken ct) =>
        {
            var result = await sup.StartAsync(name, ct, IsTrue(force));
            return Results.Json(result, WatchdogJsonContext.Default.ActionResult,
                statusCode: StatusFor(result));
        });

        // NB: deliberately NOT the request's CancellationToken (same reasoning as DELETE /instance/{name}
        // below). A stop drains for the instance's full stop_command_timeout_seconds before hard-killing —
        // routinely longer than a caller's HTTP timeout. Passing the request token let a client disconnect
        // abort the stop mid-drain, leaving the instance killed but still tabled as Running, which the
        // crash path then read as a crash and restarted: the operator's stop became a start. Once a stop
        // begins it runs to completion; its internal waits are individually bounded, so it cannot hang.
        app.MapPost("/stop/{name}", async (string name, InstanceSupervisor sup) =>
        {
            var result = await sup.StopAsync(name, CancellationToken.None);
            return Results.Json(result, WatchdogJsonContext.Default.ActionResult,
                statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        // Atomic restart (stop → drain → start) with caller provenance. The `origin` query (optional,
        // default "scheduler") names the REQUESTING LEAF — kgsm-scheduler's scheduled restart is the one
        // caller — and the emitted instance-restarted is attributed to it, so the audit trail names the
        // leaf that asked rather than this daemon. The query key stays `origin` because that is what
        // kgsm-lib's IWatchdogClient.RestartAsync sends. Does NOT touch the crash streak — it routes
        // through StartAsync which resets it. Same three statuses as /start, for the same reason: its
        // start half runs the capacity check, so a restart can be refused for room — and that lands the
        // instance DOWN with the stop already done, which a caller reading a generic 409 would retry
        // into the identical refusal. Uncancellable for the same reason as /stop — it performs a full
        // graceful stop first, and an aborted one leaves the instance down but tabled.
        app.MapPost("/restart/{name}", async (string name, string? origin, InstanceSupervisor sup) =>
        {
            var result = await sup.RestartAsync(name, string.IsNullOrWhiteSpace(origin) ? "scheduler" : origin, CancellationToken.None);
            return Results.Json(result, WatchdogJsonContext.Default.ActionResult,
                statusCode: StatusFor(result));
        });

        app.MapGet("/status/{name}", (string name, InstanceSupervisor sup) =>
        {
            var st = sup.Status(name);
            return st is null
                ? Results.StatusCode(StatusCodes.Status404NotFound)
                : Results.Json(st, WatchdogJsonContext.Default.InstanceState);
        });

        app.MapGet("/list", (InstanceSupervisor sup) =>
            Results.Json(sup.List(), WatchdogJsonContext.Default.InstanceStateArray));

        // The run clock, separate from /list because it answers for instances /list cannot: an instance
        // leaves the supervised table when it stops, and "how long has this been down" is asked of exactly
        // those. Reads the durable ledger, so it survives a daemon restart.
        app.MapGet("/runtimes", (InstanceSupervisor sup) =>
            Results.Json(sup.RunTimes(), WatchdogJsonContext.Default.InstanceRunTimesArray));

        // Boot-autostart (systemctl-style enable/disable), orthogonal to start/stop. These mutate only
        // the persisted set RestoreAsync reads at boot; they never spawn or kill.
        app.MapPost("/enable/{name}", async (string name, InstanceSupervisor sup, CancellationToken ct) =>
        {
            var result = await sup.EnableAsync(name, ct);
            return Results.Json(result, WatchdogJsonContext.Default.ActionResult,
                statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        app.MapPost("/disable/{name}", async (string name, InstanceSupervisor sup, CancellationToken ct) =>
        {
            var result = await sup.DisableAsync(name, ct);
            return Results.Json(result, WatchdogJsonContext.Default.ActionResult,
                statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        // Deregistration — the uninstall counterpart. Drops the instance from supervision entirely:
        // table entry, cgroup, boot-autostart intent, persisted counters. Idempotent (an unknown name is
        // a 200 no-op), so an uninstall never fails because the daemon had already forgotten it. 409 only
        // when the instance is still live after the stop attempt — deregistering then would orphan it.
        // NB: deliberately NOT the request's CancellationToken. Deregistering stops the instance first,
        // which can take the full graceful-stop timeout — longer than a caller's HTTP timeout. Passing the
        // request token let a client disconnect abort the stop mid-drain, leaving the instance half torn
        // down and still in the table: the exact leak this endpoint exists to close. Once a deregister
        // begins it runs to completion; its internal waits are individually bounded, so it cannot hang.
        app.MapDelete("/instance/{name}", async (string name, InstanceSupervisor sup) =>
        {
            var result = await sup.ForgetAsync(name, CancellationToken.None);
            return Results.Json(result, WatchdogJsonContext.Default.ActionResult,
                statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        // The persisted boot-autostart name set — the authoritative source for "is it enabled?".
        app.MapGet("/enabled", (InstanceSupervisor sup) =>
            Results.Json(sup.EnabledNames(), WatchdogJsonContext.Default.StringArray));

        // Live-apply a CPU-priority change to a RUNNING instance's cgroup — writes cpu.weight
        // in place, no respawn. 200 always: Ok=false + a plain message when the cgroup is absent (not
        // running), since the config is still persisted by kgsm and takes effect at the next start.
        // Memory cap has no live-apply twin: shrinking memory.max under a running game can't reclaim
        // pages it already touched, so the cap is applied only at spawn (see SpawnEngine).
        app.MapPost("/set-cpu-priority/{name}/{priority}", (string name, string priority, CgroupManager cgroups) =>
        {
            int weight = CgroupManager.CpuWeightFor(priority);
            bool applied = cgroups.SetCpuWeight(name, weight);
            var result = new ActionResult(name, applied, applied
                ? $"cpu.weight set to {weight} ({priority})"
                : "instance cgroup not found — not running; will apply at next start");
            return Results.Json(result, WatchdogJsonContext.Default.ActionResult);
        });

        // The running daemon's build identity. The hot-swap deploy curls this after a
        // reload to confirm the new binary is live; the same version is what `--version` prints. Read
        // from the compiled-in informational version (never fabricated), split into version + commit.
        app.MapGet("/version", () =>
            Results.Json(
                WatchdogVersionInfo.FromInformational(VersionInfo.Informational),
                WatchdogJsonContext.Default.WatchdogVersionInfo));

        // Live player presence across all instances: who this daemon currently sees connected, and —
        // for every instance, including the ones it sees nobody on — whether it could see anybody at
        // all.
        //
        // Both halves, because either alone lies. A bare session list makes an absent instance
        // ambiguous between "nobody is online" and "this game cannot report players", and every
        // consumer that renders the first reading of the second states something the host does not
        // know. The two travel together so a caller cannot read one without the other.
        //
        // Detection is PlayerDetection's answer — the same predicate the ingesters gate on, not a
        // second derivation from the same config. It is re-read per request: a blueprint edit or a
        // reinstall changes it, and this is a control call at human cadence, not a hot path.
        app.MapGet("/players", (PlayerSessionStore store, IInstanceService instances) =>
        {
            var sessions = store.GetAllSessionsWithKeys();
            var result = new Dictionary<string, InstancePresence>(StringComparer.Ordinal);

            // Every instance kgsm knows, not only the ones with somebody on them — an instance
            // missing from this map is the ambiguity the endpoint exists to remove.
            Dictionary<string, Instance>? inventory = null;
            try
            {
                inventory = instances.GetAllOrNull();
            }
            catch (Exception)
            {
                // Left null: reported below as detection "unknown" for whatever sessions are held,
                // which is the honest answer when the inventory could not be read. Throwing would
                // lose the sessions too.
            }

            foreach (var (name, instance) in inventory ?? [])
            {
                var players = sessions.TryGetValue(name, out var tracked)
                    ? tracked.Select(e => new PlayerSession(e.Key, e.Value.Id, e.Value.Name, e.Value.Addr)).ToArray()
                    : [];

                result[name] = new InstancePresence(PlayerDetection.For(instance).ToString().ToLowerInvariant(), players);
            }

            // A tracked instance the inventory did not carry — uninstalled mid-flight, or the whole
            // inventory unreadable. Its sessions are real and are reported; what cannot be honestly
            // claimed is the detection, so it is named "unknown" rather than guessed at.
            foreach (var (name, tracked) in sessions)
            {
                if (result.ContainsKey(name))
                    continue;

                result[name] = new InstancePresence(
                    "unknown",
                    [.. tracked.Select(e => new PlayerSession(e.Key, e.Value.Id, e.Value.Name, e.Value.Addr))]);
            }

            return Results.Json(result, WatchdogJsonContext.Default.DictionaryStringInstancePresence);
        });

        // Read an instance's router port-forwards. Read-only by design: the daemon opens them as it
        // spawns and releases them on a deliberate stop, so an instance's forwards last exactly as long as
        // its run and there is nothing for a caller to open by hand. A plain 200 whose body carries the
        // honest state (queried vs unavailable) — the daemon IS reachable even when the router is not, so
        // an unreachable IGD is in-body "unavailable", never an HTTP error or a fabricated empty list.
        app.MapGet("/upnp/{name}", async (string name, InstanceSupervisor sup, CancellationToken ct) =>
        {
            var result = await sup.ListUpnpAsync(name, ct);
            return Results.Json(result, WatchdogJsonContext.Default.UpnpListResult);
        });
    }
}

/// <summary>
/// The assembly informational version, read once. SourceLink stamps it as
/// <c>&lt;version&gt;+&lt;commit&gt;</c>; <see cref="WatchdogVersionInfo.FromInformational"/> splits it.
/// Falls back to the plain assembly version string when no informational attribute is present. Shared by
/// <c>GET /version</c> and the top-of-<c>Main</c> <c>--version</c>/<c>--selfcheck</c> branches.
/// </summary>
internal static class VersionInfo
{
    public static readonly string Informational =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";
}
