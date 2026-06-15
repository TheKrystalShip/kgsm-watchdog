using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;

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
    DesiredStateStore store,
    IEventManagementService events,
    UpnpService upnp,
    ILogger<InstanceSupervisor> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, SupervisedInstance> _instances = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The autonomous supervisor lifecycle events kgsm-lib forwards to kgsm (dash CLI form). The
    // watchdog is the only component that observes a crash, so it is the sole emitter of these.
    private const string EventCrashed = "instance-crashed";
    private const string EventFailed = "instance-failed";

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

            // Scope guard: the watchdog supervises NATIVE instances. Container instances are supervised
            // by Docker. No-op on those. (Runtime alone is the discriminator now that systemd is gone —
            // every native instance is the watchdog's.)
            if (instance.Runtime != InstanceRuntime.Native)
                return new ActionResult(name, false,
                    $"out of scope: {instance.Runtime} — the watchdog only supervises native instances");

            // Get-or-create the durable record. A manual start is an operator override: it refreshes
            // the spec, re-asserts desired=running, and clears any give-up latch or failure streak.
            var si = existing ?? new SupervisedInstance { Name = name, Spec = instance };
            si.Spec = instance;
            si.DesiredRunning = true;
            si.Restart.Reset();

            // NB: start is runtime-only — it does NOT touch the persisted boot-autostart set. Surviving a
            // reboot is a separate, explicit axis owned by enable/disable (systemctl-style); see
            // EnableAsync/DisableAsync. A bare start that is never enabled will not auto-start next boot.

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
            OpenPortForwarding(si.Spec); // fresh bring-up → open UPnP (fire-and-forget, best-effort)
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
            // NB: stop is runtime-only — it does NOT touch the persisted boot-autostart set. An instance
            // can be stopped now yet still enabled (it comes back next boot); use `disable` to drop it
            // from auto-start. This is the systemctl-style split (stop ≠ disable).

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
            // Release the UPnP mapping on a deliberate stop (fire-and-forget, off the drain — so a stop
            // that throws or hard-kills mid-drain still closes it). Held across crash-restarts, only an
            // intended stop closes it. No-op unless enable_port_forwarding.
            ClosePortForwarding(si.Spec);

            // No live handle. Either there genuinely is no process (RestartPending / Stopped / Failed —
            // just cancel and forget), or this is an ADOPTED-live instance: re-attached after a daemon
            // restart, supervised by cgroup alone, with no FIFO/PID recovered. We cannot send its
            // graceful stop command, so a populated cgroup must be torn down with cgroup.kill + drain
            // (not the no-op "not running"). Graceful stop returns on the instance's next respawn, which
            // rebuilds a real handle.
            if (si.Current is null)
            {
                string reason;
                if (cgroups.IsPopulated(name))
                {
                    cgroups.Kill(name);
                    await WaitForDrainAsync(name, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    cgroups.Remove(name);
                    reason = "stopped (adopted instance hard-killed; no graceful-stop channel until a respawn)";
                }
                else
                {
                    PurgeCgroup(name);
                    reason = si.Phase == SupervisionPhase.RestartPending ? "stopped (cancelled pending restart)" : "not running";
                }
                _instances.TryRemove(name, out _);
                return new ActionResult(name, true, reason);
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

    // ---- boot-autostart (enable/disable) ----------------------------------------------------
    // The systemctl-style boot axis, orthogonal to start/stop. These mutate ONLY the persisted set
    // (which RestoreAsync reads at boot); they never spawn or kill. `enable` does not start the
    // instance, `disable` does not stop it — exactly like `systemctl enable`/`disable`.

    /// <summary>
    /// Mark <paramref name="name"/> for boot auto-start (idempotent). Validates it is a known, in-scope
    /// (native) instance — like <c>systemctl enable</c> refusing an unknown unit — then persists intent.
    /// Does NOT require the supervisor to be ready or the instance to be running: enablement is offline
    /// intent that survives until an explicit <c>disable</c>.
    /// </summary>
    public async Task<ActionResult> EnableAsync(string name, CancellationToken ct = default)
    {
        // Read the spec OUTSIDE the gate (it shells out to kgsm-lib and can be slow).
        var instance = instances.GetInstanceInfo(name);
        if (instance is null)
            return new ActionResult(name, false, "unknown instance (kgsm-lib returned no info)");
        if (instance.Runtime != InstanceRuntime.Native)
            return new ActionResult(name, false,
                $"out of scope: {instance.Runtime} — only native instances can be enabled for watchdog auto-start");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { store.Add(name); }
        finally { _gate.Release(); }

        logger.LogInformation("enabled {Instance} for boot auto-start", name);
        return new ActionResult(name, true, "enabled (will auto-start on boot)");
    }

    /// <summary>
    /// Drop <paramref name="name"/> from boot auto-start (idempotent). Pure persistence — never stops a
    /// running instance (use <c>stop</c> for that). Accepts any name so it can prune a stale/removed
    /// entry; no validation needed to forget intent.
    /// </summary>
    public async Task<ActionResult> DisableAsync(string name, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { store.Remove(name); }
        finally { _gate.Release(); }

        logger.LogInformation("disabled {Instance} for boot auto-start", name);
        return new ActionResult(name, true, "disabled (will not auto-start on boot)");
    }

    /// <summary>The persisted boot-autostart name set (powers <c>GET /enabled</c>).</summary>
    public string[] EnabledNames() => store.Load().ToArray();

    // ---- boot restore (Inc 4) ----------------------------------------------------------------

    /// <summary>
    /// At daemon startup, restore supervision of every instance the operator enabled for boot auto-start
    /// (the set persisted by <see cref="DesiredStateStore"/>) — the in-house replacement for systemd boot
    /// auto-start. For each, the spec is re-read fresh from kgsm-lib, then <see cref="RestorePlan"/>
    /// decides: ADOPT a still-live cgroup (a process that outlived a daemon restart — no kill, no
    /// respawn), SPAWN a dead one (a host reboot left nothing running), or skip.
    /// <para>
    /// Deliberately does <b>not</b> route through <see cref="StartAsync"/>: that purges the cgroup
    /// before spawning (<c>cgroup.kill</c>), which would murder a live instance we mean to adopt. And it
    /// never prunes the persisted set on a (possibly transient) kgsm-lib read miss — durability is the
    /// whole point; only an explicit stop removes intent.
    /// </para>
    /// </summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        var names = store.Load();
        if (names.Count == 0)
        {
            logger.LogInformation("no persisted desired-state to restore");
            return;
        }
        if (!state.Ready)
        {
            logger.LogError(
                "supervisor not ready ({Detail}); cannot restore {Count} instance(s) — they will not auto-start this boot",
                state.Detail, names.Count);
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            int adopted = 0, spawned = 0, failed = 0, skipped = 0;

            foreach (var name in names)
            {
                if (_instances.ContainsKey(name))
                    continue; // already tracked — restore is idempotent

                Instance? spec = null;
                try { spec = instances.GetInstanceInfo(name); }
                catch (Exception ex) { logger.LogWarning(ex, "restore: kgsm-lib threw reading {Instance}", name); }

                bool populated = cgroups.IsPopulated(name);
                switch (RestorePlan.Classify(populated, spec))
                {
                    case RestoreAction.Adopt:
                        AdoptLive(name, spec!, now);
                        adopted++;
                        break;

                    case RestoreAction.Spawn:
                        if (RespawnFresh(name, spec!, now)) spawned++; else failed++;
                        break;

                    case RestoreAction.SkipGone:
                        skipped++;
                        logger.LogWarning(
                            "restore: {Instance} is in the auto-start set but kgsm-lib returned no config — skipping " +
                            "(intent kept; re-start manually if this was a transient read failure, or stop it to prune)", name);
                        break;

                    case RestoreAction.SkipOutOfScope:
                        skipped++;
                        logger.LogWarning(
                            "restore: {Instance} is no longer a native instance ({Runtime}) — skipping",
                            name, spec!.Runtime);
                        break;
                }
            }

            logger.LogInformation(
                "restore complete: {Adopted} adopted, {Spawned} spawned, {Failed} failed, {Skipped} skipped (of {Total} persisted)",
                adopted, spawned, failed, skipped, names.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Re-attach supervision to an instance whose cgroup is already populated (it outlived a daemon
    /// restart). <see cref="SupervisedInstance.Current"/> stays null — the previous daemon held the PID
    /// and FIFO, and neither is recoverable — so the reconcile loop supervises it by cgroup liveness
    /// alone. On its first crash it respawns through the normal path, rebuilding a real handle; until
    /// then a stop is a hard <c>cgroup.kill</c> and there is no exit code (<c>ShouldRestartAfter(null)</c>
    /// restarts under both policies, so it stays supervised).
    /// </summary>
    private void AdoptLive(string name, Instance spec, DateTime now)
    {
        var si = new SupervisedInstance
        {
            Name = name,
            Spec = spec,
            DesiredRunning = true,
            Phase = SupervisionPhase.Running,
            SpawnedAt = now, // treat as freshly (re)spawned so the grace window applies
            LastReason = "re-adopted live cgroup after daemon restart (no graceful-stop channel until next respawn)",
        };
        _instances[name] = si;
        logger.LogInformation("restore: adopted live {Instance} (cgroup populated; Current=null until next respawn)", name);
    }

    /// <summary>
    /// Spawn an instance whose cgroup is empty (a host reboot / clean stop left nothing running). Mirrors
    /// the RESTART path (<see cref="ReconcileRestartPending"/>), not <see cref="StartAsync"/>: a bare
    /// <see cref="TrySpawn"/> that trusts the grace window, NOT a synchronous <c>WaitForPopulated</c> —
    /// blocking 5s × N instances here would delay the control socket binding during boot. The reconcile
    /// loop confirms liveness a tick later. Returns true if it spawned, false if the spawn failed
    /// (tracked as <see cref="SupervisionPhase.Failed"/>).
    /// </summary>
    private bool RespawnFresh(string name, Instance spec, DateTime now)
    {
        var si = new SupervisedInstance { Name = name, Spec = spec, DesiredRunning = true };
        if (TrySpawn(si, now, out var reason))
        {
            si.LastReason = $"restored after restart; {reason}";
            _instances[name] = si;
            // Boot bring-up of a DEAD instance (a host reboot left nothing running) — a first bring-up
            // of the supervision episode, not a crash-restart, and exactly when the router lease is most
            // likely gone. Open UPnP (upnpc -r is idempotent, so a surviving lease is harmless). Only the
            // ADOPTED-live path (AdoptLive) deliberately does NOT re-assert — its mapping persisted.
            OpenPortForwarding(si.Spec);
            logger.LogInformation("restore: spawned {Instance} ({Reason})", name, reason);
            return true;
        }

        si.Phase = SupervisionPhase.Failed;
        si.LastReason = $"restore spawn failed: {reason}";
        _instances[name] = si;
        logger.LogError("restore: spawn failed for {Instance}: {Reason}", name, reason);
        return false;
    }

    public InstanceState? Status(string name)
    {
        // The enabled set is independent of the live table — an instance can be enabled-but-not-tracked
        // (enabled, never started this session). Load once and test membership.
        bool enabled = store.Load().Contains(name, StringComparer.Ordinal);

        if (_instances.TryGetValue(name, out var si))
            return ToState(si, enabled);

        // Untracked but possibly alive (e.g. orphaned across a daemon restart — re-adoption is Inc 3).
        if (cgroups.IsPopulated(name))
            return new InstanceState(name, "unknown", enabled, true, null, cgroups.PathFor(name), "unknown", 0,
                "untracked live cgroup (orphan from a previous daemon?)");

        // Not tracked and not live → 404 (stable contract for __watchdog_is_active/__watchdog_tracks).
        // The boot-autostart bit is reported authoritatively by GET /enabled, not here.
        return null;
    }

    public InstanceState[] List()
    {
        var enabled = new HashSet<string>(store.Load(), StringComparer.Ordinal);
        return _instances.Values.Select(si => ToState(si, enabled.Contains(si.Name))).ToArray();
    }

    private InstanceState ToState(SupervisedInstance si, bool enabled) => new(
        si.Name,
        si.DesiredText,
        enabled,
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
            EmitSupervisionEvent(EventFailed, name, exit, si.Restart.ConsecutiveFailures);
            return;
        }

        si.Phase = SupervisionPhase.RestartPending;
        si.NextRestartAt = now + delay.Value;
        si.LastReason = $"{verb} ({exitText}); restart #{si.Restart.ConsecutiveFailures} in {(int)delay.Value.TotalSeconds}s";
        logger.LogWarning("{Instance} {Verb} ({Exit}); restart #{N} in {Delay}",
            name, verb, exitText, si.Restart.ConsecutiveFailures, delay.Value);
        EmitSupervisionEvent(EventCrashed, name, exit, si.Restart.ConsecutiveFailures);
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
            // The respawn never produced a process, so there is no exit code to report (honest unknown).
            EmitSupervisionEvent(EventFailed, si.Name, null, si.Restart.ConsecutiveFailures);
            return;
        }
        si.NextRestartAt = now + delay.Value;
        si.LastReason = $"restart failed ({reason}); retry #{si.Restart.ConsecutiveFailures} in {(int)delay.Value.TotalSeconds}s";
        logger.LogWarning("{Instance} restart failed ({Reason}); retry #{N} in {Delay}",
            si.Name, reason, si.Restart.ConsecutiveFailures, delay.Value);
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// Fire-and-forget, best-effort UPnP open on a fresh bring-up (a manual <c>start</c> or a boot
    /// respawn of a dead instance — NOT a crash-restart, where the router lease is deliberately held).
    /// Off-loaded to the thread pool so a slow/absent router never delays the start result or holds the
    /// supervisor gate; the service self-gates on <c>enable_port_forwarding</c> and time-boxes upnpc.
    /// </summary>
    private void OpenPortForwarding(Instance spec) => _ = Task.Run(async () =>
    {
        try { await upnp.OpenAsync(spec).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogWarning(ex, "UPnP open task faulted for {Instance}", spec.Name); }
    });

    /// <summary>
    /// Fire-and-forget, best-effort UPnP close on a deliberate stop. Same off-the-gate, swallow-failures
    /// posture as <see cref="OpenPortForwarding"/>.
    /// </summary>
    private void ClosePortForwarding(Instance spec) => _ = Task.Run(async () =>
    {
        try { await upnp.CloseAsync(spec).ConfigureAwait(false); }
        catch (Exception ex) { logger.LogWarning(ex, "UPnP close task faulted for {Instance}", spec.Name); }
    });

    /// <summary>
    /// Fire-and-forget emit of an autonomous supervision event (crash / give-up) through kgsm-lib,
    /// stamped <c>actor=system</c> / <c>origin=system</c> — an engine action no human drove. Off-loaded
    /// to the thread pool so a slow <c>kgsm.sh</c> spawn never stalls the reconcile tick (which holds the
    /// supervisor gate), and best-effort: a failed emit is logged and swallowed — a dropped event is the
    /// same honest "no backfill" boundary the downstream consumer already accepts, never crashing
    /// supervision. Carries the leader exit code (the literal <c>"unknown"</c> when unreadable — never a
    /// fabricated code) and the consecutive restart-attempt count.
    /// </summary>
    private void EmitSupervisionEvent(string dashEventName, string instanceName, int? exit, int restarts)
    {
        string exitCode = exit is int code ? code.ToString() : "unknown";
        string restartCount = restarts.ToString();
        _ = Task.Run(() =>
        {
            try
            {
                events.EmitWithProvenance(
                    dashEventName, "system", "system", instanceName, exitCode, restartCount);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to emit {Event} for {Instance} (event dropped)", dashEventName, instanceName);
            }
        });
    }

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
