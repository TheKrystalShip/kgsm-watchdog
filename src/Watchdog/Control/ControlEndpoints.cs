using System.Reflection;
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
    public static void MapWatchdog(this WebApplication app)
    {
        // Unified ecosystem health probe — one `/health` everywhere (PLAN §5). This carries
        // *readiness* semantics, not bare liveness: 200 only when the supervisor is in-slice and
        // able to spawn; 503 + reason when up-but-unable; no answer at all when down. Consumers
        // treat anything other than 200 as "unavailable — retry until 200". The old bare-liveness
        // /healthz is gone (it had no consumers). /ready is kept as a deprecated alias for one
        // transition release so a not-yet-updated kgsm CLI / kgsm-lib never silently falls back
        // off the daemon onto the direct-spawn path; remove it once both are on /health.
        IResult Health(SupervisorState state)
        {
            var body = new ReadyState(state.Ready, state.Detail);
            return state.Ready
                ? Results.Json(body, WatchdogJsonContext.Default.ReadyState)
                : Results.Json(body, WatchdogJsonContext.Default.ReadyState, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        app.MapGet("/health", Health);
        app.MapGet("/ready", Health); // deprecated alias — drop once kgsm CLI + kgsm-lib use /health

        app.MapPost("/start/{name}", async (string name, InstanceSupervisor sup, CancellationToken ct) =>
        {
            var result = await sup.StartAsync(name, ct);
            return Results.Json(result, WatchdogJsonContext.Default.ActionResult,
                statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        app.MapPost("/stop/{name}", async (string name, InstanceSupervisor sup, CancellationToken ct) =>
        {
            var result = await sup.StopAsync(name, ct);
            return Results.Json(result, WatchdogJsonContext.Default.ActionResult,
                statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        // Atomic restart (stop → drain → start) with caller provenance. `origin` (optional query, default
        // "scheduler") flows to the emitted instance-restarted so the audit attributes it to the caller
        // (e.g. kgsm-scheduler's scheduled-restart), not system/system. Does NOT touch the crash streak —
        // it routes through StartAsync which resets it. 200/409 like start/stop (409 = restart couldn't
        // complete: stop or the ensuing start failed).
        app.MapPost("/restart/{name}", async (string name, string? origin, InstanceSupervisor sup, CancellationToken ct) =>
        {
            var result = await sup.RestartAsync(name, string.IsNullOrWhiteSpace(origin) ? "scheduler" : origin, ct);
            return Results.Json(result, WatchdogJsonContext.Default.ActionResult,
                statusCode: result.Ok ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
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

        // The persisted boot-autostart name set — the authoritative source for "is it enabled?".
        app.MapGet("/enabled", (InstanceSupervisor sup) =>
            Results.Json(sup.EnabledNames(), WatchdogJsonContext.Default.StringArray));

        // Live-apply a CPU-priority change to a RUNNING instance's cgroup (Phase 2) — writes cpu.weight
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

        // The running daemon's build identity (Inc 7 Phase 0). The hot-swap deploy curls this after a
        // reload to confirm the new binary is live; the same version is what `--version` prints. Read
        // from the compiled-in informational version (never fabricated), split into version + commit.
        app.MapGet("/version", () =>
            Results.Json(
                WatchdogVersionInfo.FromInformational(VersionInfo.Informational),
                WatchdogJsonContext.Default.WatchdogVersionInfo));

        // Live player sessions across all instances — the in-memory session map the native ingester
        // populates. Consumer (kgsm-api) uses this on startup to reconcile roster status without
        // waiting for events. Returns an empty object (not 404) when no instances have tracked sessions.
        app.MapGet("/players", (PlayerSessionStore store) =>
        {
            var all = store.GetAllSessionsWithKeys();
            var result = new Dictionary<string, PlayerSession[]>(StringComparer.Ordinal);
            foreach (var kvp in all)
            {
                result[kvp.Key] = kvp.Value
                    .Select(e => new PlayerSession(e.Key, e.Value.Id, e.Value.Name, e.Value.Addr))
                    .ToArray();
            }
            return Results.Json(result, WatchdogJsonContext.Default.DictionaryStringPlayerSessionArray);
        });
    }
}

/// <summary>
/// The assembly informational version, read once (Inc 7 Phase 0). SourceLink stamps it as
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
