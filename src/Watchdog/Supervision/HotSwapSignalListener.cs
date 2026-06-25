using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Bridges <c>SIGHUP</c> (delivered by <c>systemctl reload</c> → <c>ExecReload=/bin/kill -HUP $MAINPID</c>)
/// to the <see cref="HotSwapCoordinator"/>. SIGHUP is the locked trigger for the self-re-exec hot-swap
/// (Inc 7 / Option 3): because the swap keeps the SAME PID, systemd never sees a restart, so a plain
/// <c>reload</c> is the right verb. SIGTERM is deliberately left untouched — a clean stop still releases
/// each handle keeping the FIFO (Option 1 remains the fallback for any non-hot-swap restart).
/// <para>
/// The handler runs on the signal thread, so it must not block: it cancels the default disposition
/// (<c>ctx.Cancel = true</c> — we are NOT terminating) and kicks the coordinator onto the thread pool. The
/// coordinator's own in-progress guard collapses a burst of reloads to one swap.
/// </para>
/// </summary>
internal sealed class HotSwapSignalListener(HotSwapCoordinator coordinator, ILogger<HotSwapSignalListener> logger)
    : IHostedService
{
    private PosixSignalRegistration? _registration;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
        {
            // Not a termination — cancel the default disposition so SIGHUP doesn't kill the daemon.
            ctx.Cancel = true;
            logger.LogInformation("SIGHUP received — triggering hot-swap (self-re-exec)");
            // Off the signal thread: the coordinator may run a bounded subprocess + execv.
            _ = Task.Run(async () =>
            {
                try { await coordinator.TriggerAsync().ConfigureAwait(false); }
                catch (Exception ex) { logger.LogError(ex, "hot-swap trigger faulted"); }
            });
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        _registration = null;
        return Task.CompletedTask;
    }
}
