using System.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Watchdog.Interop;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Hands free memory back to the OS once the daemon settles — first after startup, then periodically
/// whenever activity has grown the resident set and the daemon has gone quiet again.
/// <para>
/// <b>Why this exists.</b> The watchdog is a Workstation-GC daemon (Server GC's per-core heaps were pure
/// waste for a 1 Hz poller — see Watchdog.csproj). Its steady state allocates almost nothing, so the GC is
/// essentially never triggered at idle. The settled floor itself is genuine and NOT what this reclaims: a
/// fully-initialised daemon legitimately holds ~30 MB of live managed heap (ASP.NET Core + DI + routing +
/// the source-gen JSON contexts), so RSS floors around ~50 MB (heap + ~13 MB binary code + native runtime),
/// and the GC keeps committed == live after any collection — there is no committed-but-free heap for a knob
/// (gen0size / HeapHardLimit / ConserveMemory) to give back. What DOES accumulate is growth ABOVE that floor:
/// every burst of control-plane traffic and even the 1 Hz reconcile/ingester ticks commit gen-0 regions, and
/// because no GC follows at idle, that growth is <b>never returned</b> — RSS ratchets up unbounded (measured:
/// a real daemon crept 56 MB → 100 MB over ~40 min of normal polling). This caps that creep.
/// </para>
/// <para>
/// <b>What it does.</b> Ten seconds after start it runs one unconditional trim (returning the startup
/// allocation churn), then on a <see cref="Interval"/> timer it re-trims <em>only</em> when the working set
/// has grown at least <see cref="GrowthTriggerBytes"/> since the last trim — so a settled daemon just sawtooths
/// near its floor instead of climbing. A trim is: an aggressive, compacting gen-2 collection (LOH included) to
/// release the GC's committed-but-free regions and fire <c>ArrayPool</c>'s gen-2 drop, followed by
/// <c>malloc_trim</c> to return the freed glibc-arena native memory the GC cannot touch. On the real ~30 MB
/// live heap the blocking compacting collect costs single-digit-to-tens of milliseconds — invisible against
/// the 120 s gated cadence and the 1 Hz crash-detection loop it briefly pauses.
/// </para>
/// <para>
/// A <see cref="BackgroundService"/> (not fire-and-forget from <c>StartAsync</c>, whose token is cancelled the
/// moment startup completes and would skip the settle). It is pure optimization: a failed trim is logged and
/// swallowed — the daemon must never die reclaiming memory.
/// </para>
/// </summary>
internal sealed class MemoryTrimmer(ILogger<MemoryTrimmer> logger) : BackgroundService
{
    // Long enough for the host to start, restore to run, and the first reconcile tick to pass, so the
    // first collection reclaims the startup churn rather than racing it.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(10);

    // How often to re-check for reclaimable growth. A supervisor's idle RSS is not latency-sensitive, so
    // a couple of minutes is plenty; the growth gate means most ticks are a single Environment.WorkingSet
    // read and nothing else.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(120);

    // Re-trim only when the working set has grown at least this much since the last trim. Idle drift is
    // effectively zero (the 1 Hz pollers allocate KB, and the working set was measured dead-flat over 90s of
    // idle), while a real burst of control-plane traffic grows it by ~11 MB and saturates there. 8 MB sits
    // comfortably between: a settled daemon never collects for nothing, and a daemon that has actually grown
    // is reclaimed within one interval.
    internal const long GrowthTriggerBytes = 8L << 20; // 8 MB

    // Working set as of the last trim; the gate compares against this. Set on every trim.
    private long _workingSetAtLastTrim;

    /// <summary>
    /// Pure gate behind the periodic trim: re-trim once growth since the last trim reaches the threshold.
    /// Extracted so the decision is unit-testable without driving a real GC.
    /// </summary>
    internal static bool ShouldTrim(long currentWorkingSet, long workingSetAtLastTrim)
        => currentWorkingSet - workingSetAtLastTrim >= GrowthTriggerBytes;

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

        // First trim is unconditional: return the startup allocation spike deterministically rather than
        // waiting for the growth gate (which has no baseline yet).
        Trim("startup");

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (ShouldTrim(Environment.WorkingSet, _workingSetAtLastTrim))
                    Trim("periodic");
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void Trim(string reason)
    {
        try
        {
            long committedBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
            long workingSetBefore = Environment.WorkingSet;

            // Compacting gen-2 collect: releases the GC's own committed heap and fires ArrayPool's gen-2
            // callback (dropping the pooled buffers the control plane rented), so the next line's RSS drop
            // includes them.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            // Then return the freed native (glibc-arena) memory the GC cannot: by default glibc caches freed
            // chunks rather than munmap-ing them, so without this the native side of the burst lingers on RSS.
            int released = 0;
            try { released = NativeMethods.malloc_trim(0); }
            catch (Exception ex) { logger.LogDebug(ex, "malloc_trim unavailable (non-glibc?) — skipping native arena trim"); }

            long committedAfter = GC.GetGCMemoryInfo().TotalCommittedBytes;
            long workingSetAfter = Environment.WorkingSet;
            _workingSetAtLastTrim = workingSetAfter;

            logger.LogInformation(
                "heap trim ({Reason}): GC committed {CommittedBeforeMB}->{CommittedAfterMB} MB, working set {WsBeforeMB}->{WsAfterMB} MB (malloc_trim released={Released})",
                reason, committedBefore >> 20, committedAfter >> 20, workingSetBefore >> 20, workingSetAfter >> 20, released);
        }
        catch (Exception ex)
        {
            // Warning, not Debug: if an aggressive compacting collect ever fails (e.g. AOT quirk), it must
            // be visible, not hidden below the default Information level.
            logger.LogWarning(ex, "heap trim ({Reason}) skipped", reason);
        }
    }
}
