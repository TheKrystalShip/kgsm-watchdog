using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Runs once at daemon startup to restore supervision: first every instance the operator enabled for boot
/// auto-start (the in-house replacement for systemd's <c>WantedBy=</c>), then any instance still live in
/// its cgroup that the persisted set did not cover — a started-not-enabled survivor of a daemon bounce —
/// and finally re-applies the persisted restart counters onto the rebuilt table
/// (<see cref="InstanceSupervisor.RehydrateCounters"/>), so a crash streak / give-up latch survives ANY
/// daemon death (an OOM/SIGKILL, not only a clean restart). The counter rehydrate runs LAST, after the
/// table is fully built, because it matches persisted entries against the live instances restore produced.
/// The timing is load-bearing on both sides:
/// <list type="bullet">
/// <item><b>After</b> <c>CgroupBootstrap</c> (Program invokes it synchronously before <c>app.Run</c>),
/// so the supervisor is ready and <c>HOME</c> is the dropped user's when the store resolves its path.</item>
/// <item><b>Before</b> the <c>CrashWatcher</c>'s first tick — it is registered ahead of that
/// <see cref="BackgroundService"/>, and a plain <see cref="IHostedService"/>'s <see cref="StartAsync"/>
/// is awaited to completion before the next hosted service starts, so the table is fully restored
/// before reconcile first runs.</item>
/// </list>
/// </summary>
internal sealed class StartupRestorer(InstanceSupervisor supervisor, ILogger<StartupRestorer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await supervisor.RestoreAsync(cancellationToken).ConfigureAwait(false);
            // Then re-adopt any instance still running in its cgroup that the persisted set did NOT cover
            // (started-not-enabled survivors of a daemon bounce). No-op after a host reboot (no live cgroups).
            await supervisor.AdoptLiveOrphansAsync(cancellationToken).ConfigureAwait(false);
            // Finally, re-apply persisted restart counters onto the now-complete table — the honesty fix
            // that makes a crash streak / give-up latch survive ANY daemon death, not only a clean restart.
            // MUST run last: it matches the persisted file against the live instances the steps above built.
            supervisor.RehydrateCounters(cancellationToken);
        }
        catch (Exception ex)
        {
            // Restore must never stop the daemon from coming up — a watchdog that refuses to start is
            // worse than one that didn't auto-restore. Log and continue; manual /start still works.
            logger.LogError(ex, "startup restore threw; continuing without auto-restore");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
