using System.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Hands the startup allocation spike back to the OS once the daemon goes idle.
/// <para>
/// Bringing the daemon up — building the host + DI graph, warming the source-gen JSON, the first
/// kgsm-lib reads — churns through hundreds of MB of short-lived allocations, which grows the GC
/// heap to ~85 MB <em>committed</em>. Workstation GC then never gives those pages back on its own,
/// because a settled supervisor (a 1 Hz cgroup poll) allocates almost nothing. Measured idle:
/// ~87 MB committed against only ~11 MB live — i.e. ~75 MB of committed-but-free heap on the RSS.
/// </para>
/// <para>
/// So once startup has settled, run ONE aggressive, compacting collection (LOH included) to return
/// that spike deterministically — rather than waiting on the trickle of GCs the crash-watcher's 1 Hz
/// allocations eventually drive. After this the resident set tracks live size; later start/stop
/// bursts are small and <c>System.GC.ConserveMemory</c> keeps their growth from lingering. A
/// <see cref="BackgroundService"/> (not fire-and-forget from StartAsync, whose token is cancelled the
/// moment startup completes and would silently skip the trim); it is pure optimization, so a failure
/// is logged and swallowed.
/// </para>
/// </summary>
internal sealed class StartupMemoryTrimmer(ILogger<StartupMemoryTrimmer> logger) : BackgroundService
{
    // Long enough for the host to start, restore to run, and the first reconcile tick to pass, so the
    // collection reclaims the startup churn rather than racing it.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(SettleDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // shutting down before the settle elapsed — nothing to do
        }

        try
        {
            long committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
            long workingSetBefore = Environment.WorkingSet;

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            long committedAfter = GC.GetGCMemoryInfo().TotalCommittedBytes;
            long workingSetAfter = Environment.WorkingSet;

            logger.LogInformation(
                "startup heap trim: GC committed {CommittedBeforeMB}->{CommittedAfterMB} MB, working set {WsBeforeMB}->{WsAfterMB} MB",
                committedBefore >> 20, committedAfter >> 20, workingSetBefore >> 20, workingSetAfter >> 20);
        }
        catch (Exception ex)
        {
            // Warning, not Debug: if an aggressive compacting collect ever fails (e.g. AOT quirk), it
            // must be visible, not hidden below the default Information level like the first attempt was.
            logger.LogWarning(ex, "startup heap trim skipped");
        }
    }
}
