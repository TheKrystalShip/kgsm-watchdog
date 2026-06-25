using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers <see cref="RestartTracker.Restore"/> — the disk-rehydrate seam (Inc 7 Phase 2). Restore must
/// faithfully re-seat BOTH the consecutive-failure streak and the give-up latch from a persisted
/// snapshot, so a crash-looping instance's counters survive ANY daemon death (an OOM/SIGKILL, not only
/// a planned restart) instead of silently resetting to a fabricated zero. Crucially, a restored give-up
/// latch must still be honored: a tracker rehydrated as "gave up" returns null from RegisterCrash, just
/// as if it had hit the limit live — no extra restarts after a bounce.
/// </summary>
public sealed class RestartTrackerTests
{
    private static BackoffPolicy Policy(int maxRetries = 5) => new()
    {
        BaseDelay = TimeSpan.FromMilliseconds(1000),
        MaxDelay = TimeSpan.FromMilliseconds(60_000),
        MaxRetries = maxRetries,
    };

    [Fact]
    public void Restore_sets_both_consecutive_failures_and_gave_up()
    {
        var t = new RestartTracker();
        Assert.Equal(0, t.ConsecutiveFailures);
        Assert.False(t.GaveUp);

        t.Restore(consecutiveFailures: 3, gaveUp: true);

        Assert.Equal(3, t.ConsecutiveFailures);
        Assert.True(t.GaveUp);
    }

    [Fact]
    public void Restore_can_seat_a_streak_without_giving_up()
    {
        var t = new RestartTracker();
        t.Restore(consecutiveFailures: 2, gaveUp: false);

        Assert.Equal(2, t.ConsecutiveFailures);
        Assert.False(t.GaveUp);

        // It picks up the backoff curve where it left off: the NEXT crash is failure #3.
        Assert.Equal(TimeSpan.FromSeconds(4), t.RegisterCrash(Policy())); // base·2^(3-1) = 4s
        Assert.Equal(3, t.ConsecutiveFailures);
    }

    [Fact]
    public void A_restored_gave_up_tracker_still_refuses_to_restart()
    {
        // The honesty payoff: a daemon that OOM'd while an instance was Failed must come back Failed,
        // not silently resurrect it. A give-up latch restored from disk behaves exactly like one set live.
        var t = new RestartTracker();
        t.Restore(consecutiveFailures: 5, gaveUp: true);

        Assert.Null(t.RegisterCrash(Policy()));   // still given up — no restart
        Assert.True(t.GaveUp);
        Assert.Equal(5, t.ConsecutiveFailures);   // RegisterCrash short-circuits before incrementing
    }

    [Fact]
    public void A_manual_start_still_clears_a_restored_latch()
    {
        // Restore preserves intent across a crash; an explicit operator start (Reset) still overrides it.
        var t = new RestartTracker();
        t.Restore(consecutiveFailures: 5, gaveUp: true);

        t.Reset();

        Assert.False(t.GaveUp);
        Assert.Equal(0, t.ConsecutiveFailures);
        Assert.NotNull(t.RegisterCrash(Policy())); // restarts again from scratch
    }
}
