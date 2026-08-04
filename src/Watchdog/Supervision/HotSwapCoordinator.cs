using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Watchdog.Interop;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Drives the self-re-exec hot-swap (Inc 7 / Option 3): on a SIGHUP (via <c>systemctl reload</c>) it
/// replaces the daemon's own binary in place — same PID — so every supervised game's stdin-FIFO write-fd
/// stays open continuously and the game never sees EOF (the one thing a process-restart cannot give).
/// <para>
/// This class owns only the <b>orchestration + the safety gate</b>; the table quiesce, handoff serialize,
/// cloexec-shed, and the actual <c>execv</c> live under the supervisor's gate in
/// <see cref="InstanceSupervisor.PrepareAndExecHotSwap"/> (which never returns on success). Splitting it
/// this way keeps the coordinator unit-testable: the safety-gate decision is exercised WITHOUT ever
/// executing — inject a fake <see cref="_selfcheck"/> runner and the abort path stops before
/// <c>PrepareAndExecHotSwap</c> is reached.
/// </para>
/// <para>
/// <b>Why the safety gate.</b> <c>execve</c> replaces the image only on success; a broken new binary that
/// fails to load would otherwise take the whole supervised fleet down. So before committing we run the
/// freshly-deployed binary as a cheap subprocess with <c>--selfcheck</c> (Phase 0: parses config, loads,
/// touches nothing) and require exit 0 within a bounded window. A non-zero/timeout aborts the swap and
/// leaves the proven old image running.
/// </para>
/// </summary>
internal sealed class HotSwapCoordinator
{
    /// <summary>How long the <c>--selfcheck</c> probe of the new binary may take before it's deemed a failure.</summary>
    private static readonly TimeSpan SelfcheckTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<HotSwapCoordinator> _logger;

    /// <summary>
    /// Runs <c>&lt;path&gt; --selfcheck</c> and returns its exit code (a non-zero sentinel on
    /// launch failure/timeout). Injectable so the gate decision is unit-testable without spawning.
    /// </summary>
    private readonly Func<string, int> _selfcheck;

    /// <summary>
    /// Performs the actual quiesce+produce+execv for <c>target</c>. The real impl is
    /// <see cref="InstanceSupervisor.PrepareAndExecHotSwap"/>, which NEVER returns on a successful swap
    /// (the image is replaced). Injectable ONLY so a test can assert the swap is/ isn't REACHED without
    /// executing a real exec — a test substitutes a recording stub. Returns true if it produced/exec'd
    /// (real: never returns), false if it declined.
    /// </summary>
    private readonly Func<string, bool> _executeSwap;

    /// <summary>0 = idle, 1 = a swap is in progress. Guards against a second concurrent SIGHUP.</summary>
    private int _inProgress;

    // NB: WatchdogOptions is intentionally NOT a dependency — the coordinator needs nothing from it (the
    // re-exec target is Environment.ProcessPath, the selfcheck inherits the live env, and the handoff
    // produce/exec lives in the supervisor). Injecting an unused options would only risk a CS0414 against
    // the 0-warning AOT gate. The selfcheck + execute-swap runners are the injection points, for testability.
    public HotSwapCoordinator(
        InstanceSupervisor supervisor,
        ILogger<HotSwapCoordinator> logger,
        Func<string, int>? selfcheckRunner = null,
        Func<string, bool>? executeSwap = null)
    {
        _logger = logger;
        _selfcheck = selfcheckRunner ?? RunSelfcheckSubprocess;
        _executeSwap = executeSwap ?? supervisor.PrepareAndExecHotSwap;
    }

    /// <summary>
    /// The whole hot-swap, end to end. Idempotent against concurrent triggers (a second SIGHUP while one is
    /// running logs and returns). On the happy path it never returns — the image is replaced; the only
    /// returns are the early aborts (already-running, no self-path, failed safety gate).
    /// </summary>
    public Task TriggerAsync()
    {
        // 1. One swap at a time. CompareExchange so two SIGHUPs racing onto the threadpool can't both run.
        if (Interlocked.CompareExchange(ref _inProgress, 1, 0) != 0)
        {
            _logger.LogWarning("hot-swap: a swap is already in progress — ignoring this SIGHUP");
            return Task.CompletedTask;
        }

        try
        {
            // 2. Resolve the re-exec target = our own (just-overwritten) binary path.
            string? target = ReExec.SelfPath;
            if (string.IsNullOrEmpty(target))
            {
                _logger.LogError("hot-swap: could not resolve the self binary path (Environment.ProcessPath is null) — aborting");
                return Task.CompletedTask;
            }

            // 3. SAFETY GATE: prove the freshly-deployed binary at least loads + parses its config before we
            //    commit to replacing the running image. A broken deploy must NOT take the fleet down.
            int code = SafeSelfcheck(target);
            if (code != 0)
            {
                // Log-only (the plan's documented fallback): a dedicated kgsm wire event for this would
                // need a new event type registered across kgsm/kgsm-lib/kgsm-api; not worth that contract
                // churn for an operator-facing condition the journal already surfaces loudly here.
                _logger.LogCritical(
                    "hot-swap: ABORTED — `{Target} --selfcheck` exited {Code} (timeout/fail). Staying on the running image; " +
                    "games untouched. Fix the deployed binary and re-run `systemctl reload`.", target, code);
                return Task.CompletedTask;
            }

            _logger.LogInformation("hot-swap: safety gate passed (`--selfcheck` ok) — quiescing and execv'ing {Target}", target);

            // 4. Hand control to the supervisor under its gate: quiesce, serialize handoff, shed cloexec,
            //    flush, execve. On success this NEVER returns (image replaced). On a failed exec it
            //    Environment.Exit(70)s for a clean Restart=always recovery — so we likewise never return here
            //    in that case.
            _executeSwap(target);

            // Only reached if the swap step chose to return false instead of exiting (the real impl does not
            // by default). Treat as a non-fatal abort and let the daemon soldier on.
            _logger.LogError("hot-swap: produce/exec returned without replacing the image — the swap did not happen");
            return Task.CompletedTask;
        }
        finally
        {
            // Reached only on an abort (the happy path never returns). Clear the flag so a later reload works.
            Interlocked.Exchange(ref _inProgress, 0);
        }
    }

    /// <summary>Run the injected self-check, swallowing any exception into a non-zero sentinel.</summary>
    private int SafeSelfcheck(string target)
    {
        try { return _selfcheck(target); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "hot-swap: the --selfcheck probe threw — treating as a failure");
            return -1;
        }
    }

    /// <summary>
    /// The real safety-gate runner: spawn <c>&lt;path&gt; --selfcheck</c> as a subprocess inheriting the
    /// current environment, bounded by <see cref="SelfcheckTimeout"/>. Returns the child's exit code, or a
    /// non-zero sentinel on launch failure / timeout (the child is killed on timeout so it can't linger).
    /// </summary>
    private int RunSelfcheckSubprocess(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,          // inherit the current environment (Watchdog__*), no shell
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--selfcheck");

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
            {
                _logger.LogError("hot-swap: failed to start the --selfcheck subprocess (Process.Start returned null)");
                return -1;
            }

            if (!proc.WaitForExit((int)SelfcheckTimeout.TotalMilliseconds))
            {
                _logger.LogError("hot-swap: --selfcheck timed out after {Seconds}s — killing it", SelfcheckTimeout.TotalSeconds);
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return -2;
            }

            int code = proc.ExitCode;
            string stderr = SafeRead(proc.StandardError);
            if (code != 0 && stderr.Length > 0)
                _logger.LogError("hot-swap: --selfcheck failed (exit {Code}): {Stderr}", code, stderr.Trim());
            return code;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "hot-swap: running --selfcheck on {Path} threw", path);
            return -1;
        }
        finally
        {
            try { proc?.Dispose(); } catch { /* ignore */ }
        }
    }

    private static string SafeRead(System.IO.StreamReader reader)
    {
        try { return reader.ReadToEnd(); } catch { return ""; }
    }
}
