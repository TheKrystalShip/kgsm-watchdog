using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The clock behind crash detection. A <see cref="BackgroundService"/> that ticks a
/// <see cref="PeriodicTimer"/> (<see cref="WatchdogOptions.PollIntervalMs"/>, default 1 Hz) and calls
/// <see cref="InstanceSupervisor.Reconcile"/> each tick. It holds <b>no state and makes no decisions</b>
/// — every transition lives in the supervisor (one decision point, no timer-vs-handler race). PLAN's
/// "poll() per-instance <c>cgroup.events</c>" means exactly this: periodically read liveness for the
/// handful of supervised instances; a true <c>poll(2)</c>/epoll on each fd would be more elegant but is
/// unwarranted at this scale and would split the decision across an event handler.
/// <para>
/// The loop is crash-proof: a reconcile that throws is logged and the loop continues — the watchdog
/// must never be the thing that dies.
/// </para>
/// </summary>
internal sealed class CrashWatcher(
    InstanceSupervisor supervisor,
    WatchdogOptions options,
    ILogger<CrashWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, options.PollIntervalMs));
        logger.LogInformation("crash watcher started; polling every {Ms}ms", interval.TotalMilliseconds);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    // Reap any zombie left by a hot-swap-adopted instance that exited (the re-exec'd image
                    // owns no managed Process for it). Gate-free and independent of reconcile; runs every
                    // tick so a removed-but-zombied adopted child is cleaned even when the table is empty.
                    supervisor.ReapOrphanedChildren();
                    supervisor.Reconcile();
                }
                catch (Exception ex)
                {
                    // Never let the supervision loop die — a watchdog that crashes is no watchdog.
                    logger.LogError(ex, "reconcile pass threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        logger.LogInformation("crash watcher stopped");
    }
}
