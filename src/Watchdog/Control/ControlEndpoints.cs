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
        // Liveness of the daemon process itself.
        app.MapGet("/healthz", () => Results.Text("ok\n"));

        // Readiness of the *supervisor* — is it in-slice and able to spawn? Distinct from /healthz:
        // the process can be up but unable to supervise (not in kgsm.slice). 200 ready / 503 not.
        app.MapGet("/ready", (SupervisorState state) =>
        {
            var body = new ReadyState(state.Ready, state.Detail);
            return state.Ready
                ? Results.Json(body, WatchdogJsonContext.Default.ReadyState)
                : Results.Json(body, WatchdogJsonContext.Default.ReadyState, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

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
    }
}
