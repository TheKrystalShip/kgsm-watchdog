namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>When the supervisor restarts a process that exited while it was still wanted running.</summary>
public enum RestartPolicyMode
{
    /// <summary>
    /// Restart on <em>any</em> exit while desired-running, regardless of exit code — the only "stay
    /// down" signal is a deliberate <c>stop</c>. The right default for game servers: exit codes are an
    /// unreliable crash signal (many catch a fatal error and still exit 0), and operator intent is
    /// already held authoritatively in <see cref="SupervisedInstance.DesiredRunning"/>. Like systemd
    /// <c>Restart=always</c> (+ the crash-loop give-up).
    /// </summary>
    Always,

    /// <summary>
    /// Restart only on a non-zero / signal exit; a clean exit (code 0) is treated as an intentional
    /// shutdown and left stopped. Like systemd <c>Restart=on-failure</c>. Opt-in, because for a game
    /// server a code-0 exit can still be a crash — see the footgun note in <c>InstanceSupervisor</c>.
    /// </summary>
    OnFailure,
}

/// <summary>
/// The restart policy (PLAN §7, Inc 2): how long to wait before a restart, and when to stop trying.
/// Pure and side-effect-free so it is exhaustively unit-testable — the live reconcile loop owns the
/// clock and the cgroup I/O; this owns only the arithmetic.
/// <para>
/// Two independent dials, deliberately decoupled:
/// </para>
/// <list type="bullet">
/// <item><b>Delay between restarts</b> — exponential: <c>BaseDelay · 2^(failures-1)</c>, capped at
/// <see cref="MaxDelay"/>. Spaces out a flapping server so it isn't hammered.</item>
/// <item><b>Give-up</b> — <em>consecutive failures since last stability</em>, NOT failures-within-a-window.
/// A window-based flap limit is defeated by the exponential delay itself (the backoff spaces restarts
/// past the window, so the window never fills and give-up never fires). Counting consecutive failures
/// that each occur before the instance reaches <see cref="StabilityThreshold"/> uptime triggers
/// regardless of spacing, and correctly separates "flaps constantly" (gives up) from "crashed once
/// after a week up" (counter reset, retries forever).</item>
/// </list>
/// </summary>
internal sealed class BackoffPolicy
{
    /// <summary>First-restart delay; doubles each consecutive failure.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on the exponential delay.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Max consecutive restarts before giving up and reporting "failed".</summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>Uptime after which an instance is considered healthy and its failure counter resets.</summary>
    public TimeSpan StabilityThreshold { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Suppression window right after a (re)spawn: crash-detection is held off until the instance has
    /// had this long to enter its cgroup. Without it a slow-starting server is re-flagged as a crash
    /// before it populates — a self-inflicted restart loop.
    /// </summary>
    public TimeSpan GraceWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether a clean (code 0) exit is restarted. Default <see cref="RestartPolicyMode.Always"/>.</summary>
    public RestartPolicyMode Mode { get; init; } = RestartPolicyMode.Always;

    /// <summary>
    /// Given the leader's exit code (null = couldn't determine), should the instance be restarted?
    /// Under <see cref="RestartPolicyMode.Always"/> always yes; under <see cref="RestartPolicyMode.OnFailure"/>
    /// yes unless it exited cleanly with code 0. An unknown exit is treated as a crash (restart).
    /// </summary>
    public bool ShouldRestartAfter(int? exitCode)
        => Mode == RestartPolicyMode.Always || exitCode is not 0;

    /// <summary>
    /// Delay before the <paramref name="consecutiveFailures"/>-th restart (1-based). Overflow-safe:
    /// the shift is capped, and the result is clamped to <see cref="MaxDelay"/>.
    /// </summary>
    public TimeSpan DelayFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1)
            return Clamp(BaseDelay);

        // Cap the shift well below the double-overflow point; MaxDelay clamps the rest.
        int shift = Math.Min(consecutiveFailures - 1, 32);
        double ms = BaseDelay.TotalMilliseconds * Math.Pow(2, shift);
        if (double.IsInfinity(ms) || ms > MaxDelay.TotalMilliseconds)
            return MaxDelay;
        return Clamp(TimeSpan.FromMilliseconds(ms));
    }

    private TimeSpan Clamp(TimeSpan d) => d > MaxDelay ? MaxDelay : d;

    public static BackoffPolicy FromOptions(WatchdogOptions o) => new()
    {
        BaseDelay = TimeSpan.FromMilliseconds(o.RestartBaseDelayMs),
        MaxDelay = TimeSpan.FromMilliseconds(o.RestartMaxDelayMs),
        MaxRetries = o.RestartMaxRetries,
        StabilityThreshold = TimeSpan.FromSeconds(o.RestartStabilitySeconds),
        GraceWindow = TimeSpan.FromSeconds(o.RestartGraceSeconds),
        Mode = o.RestartPolicy,
    };
}

/// <summary>
/// Per-instance restart bookkeeping. Lives on the <see cref="SupervisedInstance"/> so it <b>survives
/// respawns and the down-window</b> (a live <see cref="RunningInstance"/> exists only while the game
/// is up; the failure count must outlast it). Mutated only under the supervisor's gate, so no locking.
/// </summary>
internal sealed class RestartTracker
{
    /// <summary>Consecutive failures since the instance last reached stability (or since start).</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>True once the crash-loop limit was hit; no further automatic restarts until a manual start.</summary>
    public bool GaveUp { get; private set; }

    /// <summary>
    /// Record a crash. Returns the delay to wait before restarting, or <c>null</c> if the crash-loop
    /// limit is now exceeded (caller marks the instance "failed").
    /// </summary>
    public TimeSpan? RegisterCrash(BackoffPolicy policy)
    {
        if (GaveUp)
            return null;

        ConsecutiveFailures++;
        if (ConsecutiveFailures > policy.MaxRetries)
        {
            GaveUp = true;
            return null;
        }
        return policy.DelayFor(ConsecutiveFailures);
    }

    /// <summary>The instance has been up past the stability threshold — it's healthy; clear the streak.</summary>
    public void NoteStable() => ConsecutiveFailures = 0;

    /// <summary>
    /// Re-seat the counters from a persisted snapshot on boot, so a crash-streak / give-up latch survives
    /// <b>any</b> daemon death (a hot-swap, but also an unclean SIGKILL/OOM) instead of silently resetting
    /// to zero. Distinct from <see cref="Reset"/> (which clears the latch for a manual start): this
    /// <em>preserves</em> honesty across a restart the operator did not ask for. Mutated only under the
    /// supervisor's gate at rehydrate time, like the rest of the tracker.
    /// </summary>
    internal void Restore(int consecutiveFailures, bool gaveUp)
    {
        ConsecutiveFailures = consecutiveFailures;
        GaveUp = gaveUp;
    }

    /// <summary>A manual start clears both the streak and the give-up latch (the operator overrides).</summary>
    public void Reset()
    {
        ConsecutiveFailures = 0;
        GaveUp = false;
    }
}
