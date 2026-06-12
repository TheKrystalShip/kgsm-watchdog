using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Runs once at daemon startup to restore supervision of every instance the operator left
/// desired-running — the boot auto-start that replaces systemd's <c>WantedBy=</c>. The timing is
/// load-bearing on both sides:
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
