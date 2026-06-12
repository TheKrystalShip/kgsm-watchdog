using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The restart policy is the one piece of Inc 2 that is pure and fully unit-testable (the live
/// reconcile loop needs real cgroups). These pin the two behaviours the exit criteria hinge on:
/// exponential spacing between restarts, and give-up driven by <em>consecutive failures since
/// stability</em> — the model that a window-based flap limit gets wrong once backoff spaces restarts
/// past the window.
/// </summary>
public sealed class BackoffPolicyTests
{
    private static BackoffPolicy Policy(int maxRetries = 5, double baseMs = 1000, double maxMs = 60_000) => new()
    {
        BaseDelay = TimeSpan.FromMilliseconds(baseMs),
        MaxDelay = TimeSpan.FromMilliseconds(maxMs),
        MaxRetries = maxRetries,
    };

    [Fact]
    public void DelayFor_grows_exponentially()
    {
        var p = Policy(baseMs: 1000, maxMs: 1_000_000);
        Assert.Equal(TimeSpan.FromSeconds(1), p.DelayFor(1));
        Assert.Equal(TimeSpan.FromSeconds(2), p.DelayFor(2));
        Assert.Equal(TimeSpan.FromSeconds(4), p.DelayFor(3));
        Assert.Equal(TimeSpan.FromSeconds(8), p.DelayFor(4));
        Assert.Equal(TimeSpan.FromSeconds(16), p.DelayFor(5));
    }

    [Fact]
    public void DelayFor_is_clamped_to_max()
    {
        var p = Policy(baseMs: 1000, maxMs: 10_000);
        Assert.Equal(TimeSpan.FromSeconds(10), p.DelayFor(10));
        // and is overflow-safe for absurd failure counts (no Infinity / negative wrap)
        Assert.Equal(TimeSpan.FromSeconds(10), p.DelayFor(1000));
    }

    [Fact]
    public void RegisterCrash_restarts_up_to_maxretries_then_gives_up()
    {
        var p = Policy(maxRetries: 3, baseMs: 1000, maxMs: 1_000_000);
        var t = new RestartTracker();

        Assert.Equal(TimeSpan.FromSeconds(1), t.RegisterCrash(p)); // failure 1 -> restart
        Assert.Equal(TimeSpan.FromSeconds(2), t.RegisterCrash(p)); // failure 2 -> restart
        Assert.Equal(TimeSpan.FromSeconds(4), t.RegisterCrash(p)); // failure 3 -> restart
        Assert.False(t.GaveUp);

        Assert.Null(t.RegisterCrash(p));                            // failure 4 -> give up
        Assert.True(t.GaveUp);
        Assert.Equal(4, t.ConsecutiveFailures);

        // Once given up, it stays given up (no further restarts) until an explicit Reset.
        Assert.Null(t.RegisterCrash(p));
        Assert.True(t.GaveUp);
    }

    [Fact]
    public void NoteStable_resets_the_streak_so_give_up_never_accumulates_across_healthy_runs()
    {
        // The crux of the advisor's correction: an instance that crashes a few times, recovers and
        // runs healthily, then crashes again much later must NOT be one step closer to "failed".
        var p = Policy(maxRetries: 2, baseMs: 1000, maxMs: 1_000_000);
        var t = new RestartTracker();

        t.RegisterCrash(p);            // 1
        t.RegisterCrash(p);            // 2 (at the limit)
        Assert.Equal(2, t.ConsecutiveFailures);

        t.NoteStable();                // ran past the stability threshold -> healthy
        Assert.Equal(0, t.ConsecutiveFailures);
        Assert.False(t.GaveUp);

        // A fresh crash starts backoff from the base again and does NOT give up.
        Assert.Equal(TimeSpan.FromSeconds(1), t.RegisterCrash(p));
        Assert.False(t.GaveUp);
    }

    [Fact]
    public void Reset_clears_the_give_up_latch_for_a_manual_start()
    {
        var p = Policy(maxRetries: 1);
        var t = new RestartTracker();

        t.RegisterCrash(p);            // 1
        Assert.Null(t.RegisterCrash(p)); // 2 -> give up
        Assert.True(t.GaveUp);

        t.Reset();                     // operator manually starts it
        Assert.False(t.GaveUp);
        Assert.Equal(0, t.ConsecutiveFailures);
        Assert.NotNull(t.RegisterCrash(p)); // restarts again from scratch
    }

    [Fact]
    public void FromOptions_maps_every_knob()
    {
        var p = BackoffPolicy.FromOptions(new WatchdogOptions
        {
            RestartBaseDelayMs = 500,
            RestartMaxDelayMs = 30_000,
            RestartMaxRetries = 7,
            RestartStabilitySeconds = 120,
            RestartGraceSeconds = 4,
            RestartPolicy = RestartPolicyMode.OnFailure,
        });

        Assert.Equal(TimeSpan.FromMilliseconds(500), p.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), p.MaxDelay);
        Assert.Equal(7, p.MaxRetries);
        Assert.Equal(TimeSpan.FromMinutes(2), p.StabilityThreshold);
        Assert.Equal(TimeSpan.FromSeconds(4), p.GraceWindow);
        Assert.Equal(RestartPolicyMode.OnFailure, p.Mode);
    }

    [Fact]
    public void Default_mode_is_always()
    {
        Assert.Equal(RestartPolicyMode.Always, new BackoffPolicy().Mode);
        Assert.Equal(RestartPolicyMode.Always, new WatchdogOptions().RestartPolicy);
    }

    [Fact]
    public void ShouldRestartAfter_always_restarts_every_exit()
    {
        var p = new BackoffPolicy { Mode = RestartPolicyMode.Always };
        Assert.True(p.ShouldRestartAfter(0));    // even a clean exit
        Assert.True(p.ShouldRestartAfter(1));
        Assert.True(p.ShouldRestartAfter(137));  // SIGKILL
        Assert.True(p.ShouldRestartAfter(null)); // unknown
    }

    [Fact]
    public void ShouldRestartAfter_onfailure_leaves_clean_exit_stopped()
    {
        var p = new BackoffPolicy { Mode = RestartPolicyMode.OnFailure };
        Assert.False(p.ShouldRestartAfter(0));   // clean exit -> stay down
        Assert.True(p.ShouldRestartAfter(1));
        Assert.True(p.ShouldRestartAfter(139));  // SIGSEGV
        Assert.True(p.ShouldRestartAfter(null)); // unknown -> treat as a crash, restart
    }
}
