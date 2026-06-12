using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The desired-state registry and the supervision brain. Holds one durable <see cref="SupervisedInstance"/>
/// per instance the daemon was told to run, and drives every state transition: the control verbs
/// (<c>start</c>/<c>stop</c>/<c>status</c>/<c>list</c>) <em>and</em> the periodic <see cref="Reconcile"/>
/// the <see cref="CrashWatcher"/> calls. Keeping every transition here — one decision point — is what
/// avoids the classic supervisor bug of two writers (a timer and an exit-handler) racing the same state.
/// <para>
/// <b>Detection vs. intent.</b> A crash is "the instance's cgroup emptied (<c>cgroup.events</c>
/// populated→0) while we still want it running" — child-inclusive and race-free. Whether that exit was
/// a crash or a clean shutdown is read from the leader's exit code (on-failure policy: exit 0 ⇒ leave
/// it; anything else ⇒ restart). Whether the operator wanted it stopped is <see cref="SupervisedInstance.DesiredRunning"/>,
/// which a deliberate <c>stop</c> clears. cgroups solve detection; desired-state solves intent.
/// </para>
/// <para>
/// Reads (status/list) are lock-free over a <see cref="ConcurrentDictionary{TKey,TValue}"/>; every
/// mutation — the two control verbs and reconcile — runs under one gate, so a long graceful-stop drain
/// can't interleave with a restart of the same instance. Reconcile <em>try-acquires</em> the gate and
/// skips the tick if a control verb holds it (it'll catch up next tick), so the watcher never blocks
/// behind a drain.
/// </para>
/// </summary>
internal sealed class InstanceSupervisor(
    IInstanceService instances,
    SpawnEngine spawnEngine,
    CgroupManager cgroups,
    BackoffPolicy policy,
    SupervisorState state,
    ILogger<InstanceSupervisor> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, SupervisedInstance> _instances = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    // ---- control verbs ----------------------------------------------------------------------

    public async Task<ActionResult> StartAsync(string name, CancellationToken ct = default)
    {
        if (!state.Ready)
            return new ActionResult(name, false, $"supervisor not ready: {state.Detail}");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _instances.TryGetValue(name, out var existing);
            if (existing is { Phase: SupervisionPhase.Running, Current: not null } && cgroups.IsPopulated(name))
                return new ActionResult(name, true, "already running");

            var instance = instances.GetInstanceInfo(name);
            if (instance is null)
                return new ActionResult(name, false, "unknown instance (kgsm-lib returned no info)");

            // Scope guard (PLAN §8): the watchdog supervises NATIVE STANDALONE instances. systemd
            // instances are supervised by systemd; container instances by Docker. No-op on those.
            if (instance.Runtime != InstanceRuntime.Native || instance.LifecycleManager != LifecycleManager.Standalone)
                return new ActionResult(name, false,
                    $"out of scope: {instance.Runtime}/{instance.LifecycleManager} — the watchdog only supervises native standalone instances");

            // Get-or-create the durable record. A manual start is an operator override: it refreshes
            // the spec, re-asserts desired=running, and clears any give-up latch or failure streak.
            var si = existing ?? new SupervisedInstance { Name = name, Spec = instance };
            si.Spec = instance;
            si.DesiredRunning = true;
            si.Restart.Reset();

            // Clear any stale handle / leftover cgroup before a fresh spawn (e.g. a crashed-but-not-yet-
            // reconciled instance, or an orphan cgroup from a previous daemon).
            DisposeCurrent(si);
            PurgeCgroup(name);

            if (!TrySpawn(si, DateTime.UtcNow, out var spawnReason))
            {
                si.Phase = SupervisionPhase.Failed;
                si.LastReason = $"start failed: {spawnReason}";
                _instances[name] = si;
                logger.LogError("spawn failed for {Instance}: {Reason}", name, spawnReason);
                return new ActionResult(name, false, $"spawn failed: {spawnReason}");
            }

            // Confirm the game actually entered its cgroup, synchronously — the operator gets the truth
            // now rather than discovering a failed start on a later status poll.
            bool populated = await WaitForPopulatedAsync(name, si.Current!, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (!populated)
            {
                DisposeCurrent(si);
                PurgeCgroup(name);
                si.Phase = SupervisionPhase.Failed;
                si.LastReason = "process did not enter its cgroup (exited immediately or self-move failed)";
                _instances[name] = si;
                return new ActionResult(name, false,
                    "process did not enter its cgroup (exited immediately or self-move failed; check the instance log)");
            }

            si.LastReason = "started";
            _instances[name] = si;
            logger.LogInformation("started {Instance} (pid {Pid})", name, si.Current!.Pid);
            return new ActionResult(name, true, $"started (pid {si.Current!.Pid})");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActionResult> StopAsync(string name, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_instances.TryGetValue(name, out var si))
            {
                // Not tracked — but a previous daemon (or a crash window) may have left a live cgroup.
                if (cgroups.IsPopulated(name))
                {
                    cgroups.Kill(name);
                    await WaitForDrainAsync(name, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    cgroups.Remove(name);
                    return new ActionResult(name, true, "stopped (untracked live cgroup torn down)");
                }
                return new ActionResult(name, true, "not running");
            }

            // Intent first: this is a deliberate stop, so the reconcile loop must never restart it.
            si.DesiredRunning = false;

            // RestartPending / Stopped / Failed have no live process — just cancel and forget.
            if (si.Current is null)
            {
                PurgeCgroup(name);
                _instances.TryRemove(name, out _);
                return new ActionResult(name, true,
                    si.Phase == SupervisionPhase.RestartPending ? "stopped (cancelled pending restart)" : "not running");
            }

            // Graceful: write the stop command into the FIFO, then drain up to the instance's timeout.
            if (!string.IsNullOrEmpty(si.Current.StopCommand))
            {
                logger.LogInformation("stopping {Instance}: sending stop command", name);
                si.Current.SendLine(si.Current.StopCommand);
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(1, si.Current.StopTimeoutSeconds));
            bool drained = await WaitForDrainAsync(name, timeout, ct).ConfigureAwait(false);

            if (!drained)
            {
                // Hard teardown: atomic whole-tree SIGKILL — no pgrep -P, nothing escapes.
                logger.LogWarning("{Instance} did not stop gracefully in {Seconds}s; cgroup.kill", name, timeout.TotalSeconds);
                cgroups.Kill(name);
                await WaitForDrainAsync(name, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }

            DisposeCurrent(si);
            cgroups.Remove(name);
            _instances.TryRemove(name, out _);

            logger.LogInformation("stopped {Instance}", name);
            return new ActionResult(name, true, drained ? "stopped gracefully" : "killed (timeout)");
        }
        finally
        {
            _gate.Release();
        }
    }

    public InstanceState? Status(string name)
    {
        if (_instances.TryGetValue(name, out var si))
            return ToState(si);

        // Untracked but possibly alive (e.g. orphaned across a daemon restart — re-adoption is Inc 3).
        if (cgroups.IsPopulated(name))
            return new InstanceState(name, "unknown", true, null, cgroups.PathFor(name), "unknown", 0,
                "untracked live cgroup (orphan from a previous daemon?)");

        return null;
    }

    public InstanceState[] List() => _instances.Values.Select(ToState).ToArray();

    private InstanceState ToState(SupervisedInstance si) => new(
        si.Name,
        si.DesiredText,
        cgroups.IsPopulated(si.Name),
        si.Current?.Pid,
        cgroups.PathFor(si.Name),
        si.PhaseText,
        si.Restart.ConsecutiveFailures,
        si.LastReason);

    // ---- the supervision loop ---------------------------------------------------------------

    /// <summary>
    /// One pass over the desired-state table, called by <see cref="CrashWatcher"/> on each tick.
    /// Try-acquires the gate so it never blocks behind a control verb's drain — a skipped tick simply
    /// retries on the next. All work is synchronous (cgroup reads + fork), so the gate is held briefly.
    /// </summary>
    public void Reconcile()
    {
        if (_instances.IsEmpty)
            return;
        if (!_gate.Wait(0))
            return; // a control verb holds the gate; catch up next tick

        try
        {
            var now = DateTime.UtcNow;
            foreach (var si in _instances.Values)
            {
                try { ReconcileOne(si, now); }
                catch (Exception ex) { logger.LogError(ex, "reconcile {Instance} failed", si.Name); }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReconcileOne(SupervisedInstance si, DateTime now)
    {
        switch (si.Phase)
        {
            case SupervisionPhase.Running:
                ReconcileRunning(si, now);
                break;
            case SupervisionPhase.RestartPending:
                ReconcileRestartPending(si, now);
                break;
            // Stopped / Failed are terminal until a manual start.
        }
    }

    private void ReconcileRunning(SupervisedInstance si, DateTime now)
    {
        string name = si.Name;

        if (cgroups.IsPopulated(name))
        {
            // Healthy. Once it's been up past the stability threshold, clear the failure streak so a
            // later isolated crash starts backoff from scratch (and never wrongly counts toward give-up).
            if (si.SpawnedAt is DateTime s && now - s >= policy.StabilityThreshold && si.Restart.ConsecutiveFailures > 0)
            {
                si.Restart.NoteStable();
                logger.LogInformation("{Instance} stable; restart counter reset", name);
            }
            return;
        }

        // Unpopulated. Suppress crash-detection during the post-spawn grace window — a slow-starting
        // server hasn't entered its cgroup yet, and flagging it now would be a self-inflicted loop.
        if (si.SpawnedAt is DateTime sp && now - sp < policy.GraceWindow)
            return;

        // It exited. Read the leader's exit code (best-effort) BEFORE disposing, then tear down.
        int? exit = si.Current?.ExitCode;
        DisposeCurrent(si);
        PurgeCgroup(name);

        string exitText = exit is int c ? $"exit {c}" : "exit unknown";

        // Default policy (Always) restarts on any exit while desired-running — the only "stay down" is
        // a deliberate /stop (which carries the operator's intent far more reliably than an exit code,
        // since game servers routinely exit 0 even on a fatal error). Opt-in on-failure instead leaves
        // a clean (code 0) exit stopped — see RestartPolicyMode for that footgun.
        if (!policy.ShouldRestartAfter(exit))
        {
            si.Phase = SupervisionPhase.Stopped;
            si.LastReason = "exited cleanly (code 0); not restarted (on-failure policy)";
            logger.LogInformation("{Instance} exited cleanly; not restarting (on-failure policy)", name);
            return;
        }

        string verb = exit == 0 ? "exited cleanly" : "crashed";
        TimeSpan? delay = si.Restart.RegisterCrash(policy);
        if (delay is null)
        {
            si.Phase = SupervisionPhase.Failed;
            si.LastReason = $"restart limit reached ({si.Restart.ConsecutiveFailures} consecutive failures, last {exitText}); gave up after {policy.MaxRetries} retries";
            logger.LogWarning("{Instance} hit the restart limit ({Count} failures, last {Exit}); giving up after {Max} retries — reporting failed",
                name, si.Restart.ConsecutiveFailures, exitText, policy.MaxRetries);
            return;
        }

        si.Phase = SupervisionPhase.RestartPending;
        si.NextRestartAt = now + delay.Value;
        si.LastReason = $"{verb} ({exitText}); restart #{si.Restart.ConsecutiveFailures} in {(int)delay.Value.TotalSeconds}s";
        logger.LogWarning("{Instance} {Verb} ({Exit}); restart #{N} in {Delay}",
            name, verb, exitText, si.Restart.ConsecutiveFailures, delay.Value);
    }

    private void ReconcileRestartPending(SupervisedInstance si, DateTime now)
    {
        if (si.NextRestartAt is DateTime due && now < due)
            return; // still waiting out the backoff delay

        logger.LogInformation("{Instance} restarting (attempt #{N})", si.Name, si.Restart.ConsecutiveFailures);
        if (TrySpawn(si, now, out var reason))
        {
            si.LastReason = $"restarted (attempt #{si.Restart.ConsecutiveFailures}); {reason}";
            return;
        }

        // The respawn itself failed to even start (e.g. a now-missing binary) — count it as a failure.
        TimeSpan? delay = si.Restart.RegisterCrash(policy);
        if (delay is null)
        {
            si.Phase = SupervisionPhase.Failed;
            si.LastReason = $"restart failed ({reason}); crash-looped ({si.Restart.ConsecutiveFailures} failures); gave up after {policy.MaxRetries} retries";
            logger.LogWarning("{Instance} restart failed and gave up after {Max} retries: {Reason}", si.Name, policy.MaxRetries, reason);
            return;
        }
        si.NextRestartAt = now + delay.Value;
        si.LastReason = $"restart failed ({reason}); retry #{si.Restart.ConsecutiveFailures} in {(int)delay.Value.TotalSeconds}s";
        logger.LogWarning("{Instance} restart failed ({Reason}); retry #{N} in {Delay}",
            si.Name, reason, si.Restart.ConsecutiveFailures, delay.Value);
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>Fork the game from the instance's cached spec into a fresh cgroup; on success the record is Running.</summary>
    private bool TrySpawn(SupervisedInstance si, DateTime now, out string reason)
    {
        try
        {
            var ri = spawnEngine.Spawn(si.Spec);
            si.Current = ri;
            si.Phase = SupervisionPhase.Running;
            si.SpawnedAt = now;
            si.NextRestartAt = null;
            reason = $"pid {ri.Pid}";
            return true;
        }
        catch (Exception ex)
        {
            si.Current = null;
            PurgeCgroup(si.Name); // SpawnEngine cleans its own partials, but be defensive
            reason = ex.Message;
            return false;
        }
    }

    private static void DisposeCurrent(SupervisedInstance si)
    {
        si.Current?.Dispose();
        si.Current = null;
    }

    /// <summary>Best-effort teardown of an instance cgroup: kill any stragglers, then remove the (empty) dir.</summary>
    private void PurgeCgroup(string name)
    {
        if (!cgroups.Exists(name))
            return;
        if (cgroups.IsPopulated(name))
            cgroups.Kill(name);
        cgroups.Remove(name);
    }

    private async Task<bool> WaitForPopulatedAsync(string name, RunningInstance ri, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cgroups.IsPopulated(name))
                return true;
            // If the launcher died before populating, stop waiting — the spawn failed.
            try { if (ri.Process.HasExited) return cgroups.IsPopulated(name); }
            catch { /* process object racing teardown */ }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return cgroups.IsPopulated(name);
    }

    private async Task<bool> WaitForDrainAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!cgroups.IsPopulated(name))
                return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return !cgroups.IsPopulated(name);
    }

    public void Dispose()
    {
        foreach (var si in _instances.Values)
            si.Current?.Dispose();
        _gate.Dispose();
    }
}
