using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Events;
using TheKrystalShip.KGSM.Watchdog.Firewall;
using TheKrystalShip.KGSM.Watchdog.Interop;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;
using System.Text;
using System.Text.Json;
using TheKrystalShip.KGSM.Lifecycle;

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
    SupervisionStateStore supervisionStore,
    RunHistoryStore runHistory,
    WatchdogJournal journal,
    LeafLifecycle lifecycle,
    UpnpService upnp,
    FirewallPortsService firewall,
    PlayerSessionStore sessions,
    ILogger<InstanceSupervisor> logger) : IDisposable, IForwardedPortClaims
{
    private readonly ConcurrentDictionary<string, SupervisedInstance> _instances = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    // PIDs of hot-swap-ADOPTED instances we must reap ourselves. A game that survives a same-PID execve
    // stays OUR child, but the re-exec'd image holds no managed Process for it (it adopted it as
    // Process=null), so when that child later exits nothing waitpid()s it and it lingers as a zombie until
    // the daemon next restarts. The 1 Hz reaper (ReapOrphanedChildren) sweeps exactly these pids — never a
    // blanket waitpid(-1), which would race the CLR reaping its OWN children and corrupt their exit codes.
    // A live pid waitpid()s to 0 (kept); a reaped or never-ours pid drops out. Bounded by the instance count.
    private readonly ConcurrentDictionary<int, byte> _reapable = new();

    // The autonomous supervisor lifecycle events kgsm-lib forwards to kgsm (dash CLI form), all
    // stamped actor=system / origin=system — engine actions no human drove. The watchdog is the
    // sole observer of a crash AND the sole driver of the recovery respawn / boot bring-up, so it
    // is the sole emitter of these. A USER-driven start/restart is NOT emitted here: that routes
    // through the kgsm command layer (exit 211/213), which emits its own event with the user's
    // provenance — emitting here too would double-emit. EventRestarted fires for the supervisor's
    // own crash-recovery respawn (ReconcileRestartPending) and for the restart THIS daemon performs
    // itself (RestartAsync, the scheduler's), which also emits EventRestartStopped in its middle;
    // EventStarted ONLY for an autonomous boot bring-up of a dead enabled instance (RespawnFresh) —
    // none of them shells kgsm.
    // (Boot-autostart is a fresh bring-up, not a crash recovery: downstream it is audited but
    // deliberately NOT linked as an alert's resolution.actionId — see kgsm-api
    // KgsmAuditConsumer.IsRecoveryAction, which excludes the system-origin start specifically.)
    private const string EventCrashed = "instance-crashed";
    private const string EventFailed = "instance-failed";
    private const string EventRestarted = "instance-restarted";
    private const string EventStarted = "instance-started";
    private const string EventRestartStopped = "instance-restart-stopped";

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
            OpenFirewallPorts(si.Spec);  // …and the host firewall rule, on the same edge
            logger.LogInformation("started {Instance} (pid {Pid})", name, si.Current!.Pid);
            return new ActionResult(name, true, $"started (pid {si.Current!.Pid})");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stop an instance: graceful stop command into the FIFO, drain up to the instance's timeout, then a
    /// whole-tree <c>cgroup.kill</c> if it didn't drain.
    /// <para>
    /// <b><paramref name="ct"/> is honored only up to the point the stop begins.</b> Past the gate every
    /// wait uses <see cref="CancellationToken.None"/>: the drain can run for the instance's full
    /// <c>stop_command_timeout_seconds</c> (plus a 5s post-kill settle), which routinely outlives a
    /// caller's HTTP timeout. Letting a client disconnect abort it mid-flight leaves the instance killed
    /// but still tabled as <c>Running</c> — a state the crash path used to read as a crash and restart,
    /// turning the operator's stop into a start. Same reasoning as <c>DELETE /instance/{name}</c>. Every
    /// wait here is separately bounded, so running to completion cannot hang.
    /// </para>
    /// </summary>
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
                    await WaitForDrainAsync(name, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
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
            // Same intent, host side: a stopped instance holds no open ports. Held across crash-restarts
            // (the rule is not what a crash breaks), so only this deliberate-stop edge closes it.
            CloseFirewallPorts(si.Spec);

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
                    await WaitForDrainAsync(name, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                    cgroups.Remove(name);
                    reason = "stopped (adopted instance hard-killed; no graceful-stop channel until a respawn)";
                }
                else
                {
                    PurgeCgroup(name);
                    reason = si.Phase == SupervisionPhase.RestartPending ? "stopped (cancelled pending restart)" : "not running";
                }
                _instances.TryRemove(name, out _);
                si.LastReason = reason;
                PersistSupervisionState(); // deliberate stop — drop this instance's persisted counters
                // A run only ends here when there was one to end: cancelling a pending restart concludes
                // nothing, and the crash that queued it already recorded that console.
                RecordRunEnd(si, RunRecord.Stopped, exit: null);
                return new ActionResult(name, true, reason);
            }

            // Graceful: write the stop command into the FIFO, then drain up to the instance's timeout.
            if (!string.IsNullOrEmpty(si.Current.StopCommand))
            {
                logger.LogInformation("stopping {Instance}: sending stop command", name);
                si.Current.SendLine(si.Current.StopCommand);
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(1, si.Current.StopTimeoutSeconds));
            bool drained = await WaitForDrainAsync(name, timeout, CancellationToken.None).ConfigureAwait(false);

            if (!drained)
            {
                // Hard teardown: atomic whole-tree SIGKILL — no pgrep -P, nothing escapes.
                logger.LogWarning("{Instance} did not stop gracefully in {Seconds}s; cgroup.kill", name, timeout.TotalSeconds);
                cgroups.Kill(name);
                await WaitForDrainAsync(name, TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }

            DisposeCurrent(si);
            cgroups.Remove(name);
            _instances.TryRemove(name, out _);

            string stopReason = drained ? "stopped gracefully" : "killed (timeout)";
            si.LastReason = stopReason;
            PersistSupervisionState(); // deliberate stop — drop this instance's persisted counters
            RecordRunEnd(si, RunRecord.Stopped, exit: null);

            logger.LogInformation("stopped {Instance}", name);
            return new ActionResult(name, true, stopReason);
        }
        finally
        {
            // Every branch above ends with this instance not running, so no session it was tracking
            // survives — see ForgetPlayerSessions. In the finally so a stop that throws mid-drain
            // still clears, and AFTER the drain so a disconnect line the game logged on its way down
            // is read against a map that still holds the session it names.
            ForgetPlayerSessions(name);
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically restart an instance: stop it, wait for its cgroup to drain, then start it again.
    /// Routes through <see cref="StopAsync"/> + <see cref="StartAsync"/>, so it deliberately does NOT
    /// count as a crash — <see cref="StartAsync"/> calls <c>si.Restart.Reset()</c>, leaving the
    /// crash-recovery streak untouched. This is the intentional-restart path (kgsm-scheduler), distinct
    /// from the autonomous respawn in <see cref="ReconcileRestartPending"/>, which is the watchdog's own.
    /// </summary>
    /// <param name="name">The instance to restart.</param>
    /// <param name="ct">Cancels the drain wait only — the graceful stop it opens with is uncancellable.</param>
    /// <param name="requester">
    /// The leaf that asked, which the emitted <c>instance-restarted</c> is ATTRIBUTED to (see
    /// <see cref="ProvenanceActor"/>) — so the audit trail names the scheduler rather than this daemon.
    /// The event's ORIGIN is <c>system</c>: origin is the surface a human drove, and a scheduled restart
    /// has none. The wire spells this query parameter <c>origin</c>, which is what kgsm-lib's
    /// <c>IWatchdogClient.RestartAsync</c> sends.
    /// </param>
    public async Task<ActionResult> RestartAsync(string name, string requester = "scheduler", CancellationToken ct = default)
    {
        var stopResult = await StopAsync(name, ct).ConfigureAwait(false);
        if (!stopResult.Ok)
            return new ActionResult(name, false, $"restart aborted — stop failed: {stopResult.Message}");

        // Wait for the cgroup to drain — a process can linger briefly after SIGKILL before its cgroup empties.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && cgroups.IsPopulated(name))
            await Task.Delay(200, ct).ConfigureAwait(false);

        // The old run is down and the new one has not been spawned yet — the middle of the restart,
        // said out loud. Neither half emits anything of its own here (StopAsync/StartAsync are the
        // control verbs, and a user-driven start/stop is kgsm's to announce), so without this the
        // first and only word is instance-restarted at the very end and every consumer reads the
        // instance as running for the whole shutdown. Attributed to the requester like the restart
        // itself. A step inside one operation, never instance-stopped — that one is the fact that
        // somebody stopped a server.
        journal.Instance(EventRestartStopped, name, ProvenanceActor(requester));

        var startResult = await StartAsync(name, ct).ConfigureAwait(false);
        if (!startResult.Ok)
            return new ActionResult(name, false, $"restart: stop ok; start failed: {startResult.Message}");

        // Emit instance-restarted attributed to the requester, so kgsm-api audits this as the scheduler
        // rather than as the watchdog's own crash recovery. Fire-and-forget, same posture as
        // EmitSystemEvent: a slow kgsm.sh spawn must not stall the caller, and a dropped event is the
        // honest no-backfill boundary the consumer already accepts.
        journal.Instance(EventRestarted, name, ProvenanceActor(requester));

        logger.LogInformation("restarted {Instance} (requester={Requester})", name, requester);
        return new ActionResult(name, true, $"restarted (requester={requester})");
    }

    // ---- UPnP read (external surface) -------------------------------------------------------
    // Opening and closing are pure lifecycle side effects (open-on-start / close-on-stop): an instance's
    // router forwards last exactly as long as its run, so there is no on-demand open to expose. The read
    // stays, because "what does the router actually hold for this instance?" is a question a diagnosing
    // operator genuinely has.

    /// <summary>
    /// List the UPnP mappings the local IGD currently holds for <paramref name="name"/> (read-only, no
    /// event). Honest by construction — an unreachable router yields state <c>"unavailable"</c>, never a
    /// fabricated empty list. The IGD filter key is the instance name (the ownership tag), so no instance
    /// lookup is needed.
    /// </summary>
    public Task<UpnpListResult> ListUpnpAsync(string name, CancellationToken ct = default)
        => upnp.ListAsync(name, ct);

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

    // ---- deregistration ----------------------------------------------------------------------

    /// <summary>
    /// Drop every trace of <paramref name="name"/> from the daemon: the live table entry, the cgroup, the
    /// boot-autostart intent, and the persisted supervision counters. This is the counterpart to
    /// <c>kgsm uninstall</c> — an instance that no longer exists must stop being supervised, or the
    /// supervisor keeps a `desired=running` record forever, restart-loops a game whose files are gone, and
    /// leaves every consumer of <c>/list</c> (the kgsm-api alert engine among them) mirroring a condition
    /// for a server nobody can act on.
    /// <para>
    /// <b>Idempotent and validation-free.</b> An unknown name succeeds as a no-op: by the time a caller
    /// deregisters, the instance's kgsm spec is typically already deleted, so validating against kgsm-lib
    /// (as <see cref="StartAsync"/>/<see cref="EnableAsync"/> do) would reject exactly the case this exists
    /// to serve. Same reasoning as <see cref="DisableAsync"/> — forgetting intent needs no proof of
    /// existence.
    /// </para>
    /// <para>
    /// <b>Refuses to orphan a live process.</b> The instance is stopped first (via <see cref="StopAsync"/>,
    /// which takes the gate itself — so this composes at the verb level, like <see cref="RestartAsync"/>).
    /// If the cgroup is STILL populated afterwards, the table entry is kept and this fails: forgetting a
    /// running game would leave a process nothing supervises and nothing can stop.
    /// </para>
    /// <para>
    /// <b>Not cancellable once begun.</b> <paramref name="ct"/> is honored only up to the point the stop
    /// starts. Callers pass <see cref="CancellationToken.None"/> (the control endpoint does): abandoning a
    /// deregister mid-stop leaves the instance half torn down AND still supervised, which is the leak this
    /// closes. Every internal wait is separately bounded, so running to completion cannot hang.
    /// </para>
    /// </summary>
    public async Task<ActionResult> ForgetAsync(string name, CancellationToken ct = default)
    {
        // Whether this was ever supervised must be sampled BEFORE the stop: StopAsync removes the table
        // entry on its own success paths, so reading it afterwards would report "not supervised" for an
        // instance we just tore down — an honest-sounding lie in the operator's log.
        bool wasTracked = _instances.ContainsKey(name);

        // Tear down anything still live. Safe + idempotent on an untracked name (it also sweeps a stale
        // cgroup left by a previous daemon), so this is unconditional rather than gated on tracking.
        await StopAsync(name, ct).ConfigureAwait(false);

        if (cgroups.IsPopulated(name))
            return new ActionResult(name, false,
                "still running after stop — refusing to deregister (would orphan the process)");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _instances.TryRemove(name, out var si);
            if (si is not null)
                DisposeCurrent(si);

            PurgeCgroup(name);   // StopAsync removes it on its live paths; this covers the rest
            store.Remove(name);  // boot-autostart intent must not resurrect a deleted instance
            sessions.Forget(name); // the stop above emptied the map; drop it, so an uninstalled instance
                                   // stops appearing in GET /players at all rather than as an empty list
            runHistory.Forget(name); // its consoles go with the instance; rows that can join to nothing
                                     // would only grow the ledger
            PersistSupervisionState();

            logger.LogInformation("deregistered {Instance} (was supervised: {WasTracked})", name, wasTracked);
            return new ActionResult(name, true, wasTracked ? "deregistered" : "not supervised (nothing to deregister)");
        }
        finally
        {
            _gate.Release();
        }
    }

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
    /// Re-adopt every instance whose cgroup is live but that is NOT already supervised — a process that
    /// was running (started, perhaps never enabled for boot auto-start) and outlived a daemon restart.
    /// Runs once at startup, right after <see cref="RestoreAsync"/>, so a daemon bounce never silently
    /// drops supervision of a live game; the persisted-set restore alone left started-not-enabled
    /// instances orphaned. After a host reboot there are no live cgroups, so this is a no-op then. The
    /// decision reuses <see cref="RestorePlan.Classify"/> (cgroup populated by construction): native →
    /// adopt, container/no-config → leave it (a live cgroup with no kgsm config is not ours to claim).
    /// </summary>
    public async Task AdoptLiveOrphansAsync(CancellationToken ct = default)
    {
        if (!state.Ready)
            return; // RestoreAsync already logged why the supervisor isn't ready

        var live = cgroups.LivePopulatedInstances();
        if (live.Count == 0)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            int adopted = 0, skipped = 0;

            foreach (var name in live)
            {
                if (_instances.ContainsKey(name))
                    continue; // already restored via the persisted boot-autostart set

                Instance? spec = null;
                try { spec = instances.GetInstanceInfo(name); }
                catch (Exception ex) { logger.LogWarning(ex, "adopt-orphan: kgsm-lib threw reading {Instance}", name); }

                if (RestorePlan.Classify(cgroupPopulated: true, spec) == RestoreAction.Adopt)
                {
                    AdoptLive(name, spec!, now);
                    adopted++;
                }
                else
                {
                    skipped++;
                    logger.LogWarning(
                        "adopt-orphan: live cgroup {Instance} is not an adoptable native instance — leaving it unsupervised", name);
                }
            }

            logger.LogInformation(
                "adopt-orphan complete: {Adopted} live instance(s) re-adopted after a daemon restart, {Skipped} skipped",
                adopted, skipped);
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
            LastReason = "re-adopted live cgroup after daemon restart",
        };

        // Recover the command channel. The game's stdin FIFO now survives a daemon bounce (the daemon
        // releases its fd without deleting the node — RunningInstance.ReleaseKeepingFifo), so re-open it to
        // restore console + graceful stop immediately, no respawn needed. The PID comes from the cgroup —
        // we are not the parent. If the FIFO is gone (an instance spawned by a pre-fix daemon that deleted
        // it on exit), fall back to cgroup-only supervision until the next respawn rebuilds the channel.
        int? pid = cgroups.FirstPid(name);
        int fd = spawnEngine.ReopenFifo(spec.SocketFile);
        if (fd >= 0)
        {
            si.Current = RunningInstance.Adopt(
                name, fd, spec.SocketFile, spec.StopCommand, spec.StopCommandTimeoutSeconds, pid, logger);
            si.LastReason = "re-adopted live cgroup after daemon restart (FIFO re-opened — console + graceful stop restored)";
            logger.LogInformation("restore: adopted live {Instance} (pid {Pid}; FIFO re-opened — full control)", name, pid);
        }
        else
        {
            si.LastReason = "re-adopted live cgroup after daemon restart (FIFO gone; graceful stop returns on next respawn)";
            logger.LogInformation("restore: adopted live {Instance} (pid {Pid}; cgroup liveness only — FIFO unavailable)", name, pid);
        }

        _instances[name] = si;
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
            // The host firewall rule is in exactly the same position as the router lease at a clean boot:
            // the instance is coming up and nothing has opened its ports for this run.
            OpenFirewallPorts(si.Spec);
            logger.LogInformation("restore: spawned {Instance} ({Reason})", name, reason);
            // Autonomous boot bring-up of a dead enabled instance — emit instance-started (system/system)
            // so the action is auditable downstream. NOT a crash recovery (there is no firing alert to
            // heal at a clean boot), so kgsm-api records the row but does not bridge it as a
            // resolution.actionId. The ADOPTED-live path (AdoptLive) stays silent — no state change.
            journal.Instance(EventStarted, name);
            return true;
        }

        si.Phase = SupervisionPhase.Failed;
        si.LastReason = $"restore spawn failed: {reason}";
        _instances[name] = si;
        logger.LogError("restore: spawn failed for {Instance}: {Reason}", name, reason);
        return false;
    }

    /// <summary>
    /// At daemon startup — run by <see cref="StartupRestorer"/> AFTER <see cref="RestoreAsync"/> and
    /// <see cref="AdoptLiveOrphansAsync"/> — re-apply the restart counters persisted by
    /// <see cref="SupervisionStateStore"/> onto the just-restored live table. This is the honesty fix:
    /// without it, ANY daemon death (an OOM/SIGKILL just as much as a planned swap) resets a
    /// crash-looping instance's failure streak to zero, fabricating health it never earned. Re-attaching
    /// across a bounce (adopt/respawn) builds a fresh <see cref="RestartTracker"/> (counter 0); this puts
    /// the truth back.
    /// <para>
    /// <b>Policy (deliberate, conservative):</b>
    /// </para>
    /// <list type="bullet">
    /// <item><b>Counters always restored.</b> For every name present in BOTH the live table and the
    /// persisted file we <em>always</em> call <see cref="RestartTracker.Restore"/> — the streak + give-up
    /// latch are pure bookkeeping with no live-process dependency, so they carry across unconditionally.
    /// This is the core fix.</item>
    /// <item><b>Phase + timing restored ONLY when not live-populated.</b> A re-adopted/respawned instance
    /// that is genuinely <em>live now</em> (a live <see cref="RunningInstance"/> handle exists OR its
    /// cgroup is populated) must keep the <c>Running</c> phase the restore just established — overwriting
    /// it with a stale persisted <c>Failed</c> would be a lie about a running game. But an instance that
    /// is NOT live now (restore found nothing to adopt/spawn, or it landed Failed/Stopped) gets its
    /// persisted phase/<c>SpawnedAt</c>/<c>NextRestartAt</c>/<c>LastReason</c> back, so a pre-crash
    /// give-up/Failed latch is honored across the bounce rather than silently cleared.</item>
    /// <item><b>Prune.</b> Persisted entries with no matching live instance are dropped (the instance was
    /// stopped/removed while the daemon was down); the rewrite reflects only what is actually supervised.</item>
    /// </list>
    /// Runs under the gate, like every other table mutation; <paramref name="ct"/> is accepted for
    /// symmetry with the other restore steps (the work itself is synchronous and brief).
    /// </summary>
    public void RehydrateCounters(CancellationToken ct = default)
    {
        var persisted = supervisionStore.Load();
        if (persisted.Instances.Count == 0 && _instances.IsEmpty)
            return;

        _gate.Wait(ct);
        try
        {
            int restored = 0, latched = 0;
            foreach (var (name, snap) in persisted.Instances)
            {
                if (!_instances.TryGetValue(name, out var si))
                    continue; // pruned below — nothing live to rehydrate onto

                // (1) Counters carry unconditionally — the core honesty fix.
                si.Restart.Restore(snap.ConsecutiveFailures, snap.GaveUp);
                restored++;

                // (2) Phase/timing only when the instance is NOT live-populated now, so a live-adopted or
                //     freshly-respawned game keeps its Running phase but a pre-crash give-up/Failed latch
                //     is honored across the bounce.
                bool liveNow = si.Current is not null || cgroups.IsPopulated(name);
                if (!liveNow)
                {
                    if (Enum.TryParse<SupervisionPhase>(snap.Phase, ignoreCase: false, out var phase))
                        si.Phase = phase;
                    si.SpawnedAt = snap.SpawnedAt;
                    si.NextRestartAt = snap.NextRestartAt;
                    if (!string.IsNullOrEmpty(snap.LastReason))
                        si.LastReason = snap.LastReason;
                    latched++;
                }
            }

            // (3) The just-rebuilt table is now authoritative — persist it (drops entries for instances
            //     that vanished while the daemon was down, and writes the rehydrated counters back).
            PersistSupervisionState();

            logger.LogInformation(
                "rehydrated supervision counters: {Restored} restored ({Latched} also re-latched phase/timing), {Persisted} on disk, {Live} live",
                restored, latched, persisted.Instances.Count, _instances.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- self-re-exec hot-swap (Inc 7 / Option 3) -------------------------------------------
    // The producer (PrepareAndExecHotSwap, run in the OLD image just before the execv) and the consumer
    // (AdoptFromHandoff, run in the NEW image at boot) are two halves of the SAME contract on either side
    // of the same-PID exec. They MUST agree on the blob shape (HotSwapHandoff) and the env-var channel.

    /// <summary>
    /// Quiesce, serialize a hot-swap handoff for every live instance, shed <c>O_CLOEXEC</c> on each FIFO
    /// fd, and <c>execv</c> the freshly-deployed binary <b>in place</b> (same PID). Runs entirely under the
    /// supervisor gate so it serializes against reconcile + every control verb — the swap is atomic w.r.t.
    /// the state table. On a SUCCESSFUL <c>execv</c> this NEVER returns (the image is replaced); the
    /// successor reads the handoff in <see cref="AdoptFromHandoff"/>.
    /// <para>
    /// <b>Exec-failure recovery = process exit, not limp.</b> If <c>execv</c> returns (it failed — e.g. the
    /// staged binary vanished between the safety-gate and the exec) we have already released every
    /// <see cref="RunningInstance"/> handle without closing its fd, so the in-memory table no longer owns a
    /// usable channel. Rather than soldier on with half-released handles, we restore the steady-state
    /// invariants (re-set <c>O_CLOEXEC</c>, unset the handoff env var) and <see cref="Environment.Exit"/>
    /// with a non-zero code so systemd's <c>Restart=always</c> bounces us; the proven Option-1 adoption path
    /// (FIFO nodes survive on disk) then recovers console + graceful stop on the fresh boot. This trades a
    /// brief, EOF-risky restart (only on the rare failed-exec, after the safety gate already passed
    /// <c>--selfcheck</c>) for a guaranteed-consistent recovery instead of a silently-degraded daemon.
    /// </para>
    /// </summary>
    /// <returns>
    /// Never returns on success. Returns <c>false</c> only on the (unreachable in practice) path where we
    /// chose not to exit after a failed exec; the default is to <see cref="Environment.Exit"/>.
    /// </returns>
    public bool PrepareAndExecHotSwap(string targetPath)
    {
        // Acquire the gate for the whole produce-and-exec critical section. Synchronous Wait() (not
        // WaitAsync) because on success we never come back to release it — the image is gone — and on
        // failure we Environment.Exit, so the gate is moot either way.
        _gate.Wait();

        // The fds we shed O_CLOEXEC on, so an aborted exec can restore the steady-state invariant.
        var shedFds = new List<int>();
        try
        {
            // 1. Build the handoff from every LIVE instance with a real FIFO fd. An adopted instance whose
            //    FIFO could not be re-opened (Current.FifoFd < 0) has no channel to carry — it stays on the
            //    cgroup-only path and the successor re-derives it via the normal restore.
            var handoff = new HotSwapHandoff();
            foreach (var si in _instances.Values)
            {
                var cur = si.Current;
                if (cur is null || cur.FifoFd < 0)
                    continue;

                handoff.Instances.Add(new HotSwapEntry
                {
                    Name = si.Name,
                    FifoFd = cur.FifoFd,
                    FifoPath = cur.FifoPath,
                    ConsecutiveFailures = si.Restart.ConsecutiveFailures,
                    GaveUp = si.Restart.GaveUp,
                    Phase = si.Phase.ToString(),
                    SpawnedAt = si.SpawnedAt,
                    NextRestartAt = si.NextRestartAt,
                    LastReason = si.LastReason,
                    DesiredRunning = si.DesiredRunning,
                });
            }

            // 2. Carry the player session maps. Every instance the ingester tracks, not just the ones with
            //    a FIFO fd above — the map is what a leave line resolves against, and the successor's log
            //    tail starts at EOF, so a map lost here cannot be rebuilt from anything (see
            //    HotSwapHandoff.PlayerSessions).
            foreach (var kvp in sessions.GetAllSessionsWithKeys())
            {
                if (kvp.Value.Count == 0)
                    continue;

                handoff.PlayerSessions[kvp.Key] = [.. kvp.Value.Select(
                    e => new PlayerSession(e.Key, e.Value.Id, e.Value.Name, e.Value.Addr))];
            }

            // 3. Belt-and-suspenders: also flush the Phase-2 disk state, so the counters survive even if the
            //    swap aborts mid-flight and the successor has to fall back to the normal restore.
            PersistSupervisionState();

            // Say goodbye as a RELOAD, before the execve that never returns. ⚠ This is the only chance:
            // the image is replaced in place, so ApplicationStopping never runs and the successor cannot
            // know what happened to its predecessor. A consumer reading `reload` knows the process id is
            // the same and that not one supervised game restarted — which is indistinguishable from an
            // outage without it, and would otherwise page somebody on a successful deploy.
            lifecycle.MarkStopping(LeafStopReason.Reload);

            // 4. Encode the blob and build an EXPLICIT envp for execve. VERIFIED 2026-06-25:
            //    Environment.SetEnvironmentVariable does NOT write through to libc environ on net10, so a
            //    handoff staged that way is invisible to a plain execv. We therefore snapshot the current
            //    environment + the handoff override (ReExec.BuildEnvp) and pass it to execve directly.
            string json = JsonSerializer.Serialize(handoff, WatchdogJsonContext.Default.HotSwapHandoff);
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            var envp = ReExec.BuildEnvp(HotSwapHandoff.EnvVarName, b64);

            // 5. Shed O_CLOEXEC on each FIFO fd so it is inherited across the execv, then release the handle
            //    WITHOUT closing the fd or deleting the FIFO node.
            foreach (var si in _instances.Values)
            {
                var cur = si.Current;
                if (cur is null || cur.FifoFd < 0)
                    continue;

                if (ReExec.ClearCloexec(cur.FifoFd))
                    shedFds.Add(cur.FifoFd);
                else
                    logger.LogWarning(
                        "hot-swap: could not clear O_CLOEXEC on {Instance} FIFO fd {Fd}; it may not survive the exec",
                        si.Name, cur.FifoFd);

                cur.ReleaseWithoutClosingFd();
            }

            logger.LogInformation(
                "hot-swap: handing off {Count} live instance(s) and {Sessions} player session(s), "
                + "execv'ing {Target} in place (same PID {Pid})",
                handoff.Instances.Count, handoff.PlayerSessions.Sum(p => p.Value.Length),
                targetPath, Environment.ProcessId);

            // 6. Flush stdout/stderr — the only managed state the successor cannot recover. execve discards
            //    everything else (threads, GC heap), so nothing else needs committing.
            Console.Out.Flush();
            Console.Error.Flush();

            // 7. The point of no return — on success control never comes back here. execve (NOT execv) with
            //    the explicit envp so the successor actually sees the handoff.
            int err = ReExec.ExecWithEnv(targetPath, new[] { targetPath }, envp);

            // --- exec FAILED (only path past the call) ---------------------------------------------------
            logger.LogCritical(
                "hot-swap: execve({Target}) FAILED with errno {Errno} — the old image is intact but its game handles " +
                "are released; exiting {Code} so Restart=always brings us back and Option-1 adoption recovers control",
                targetPath, err, 70);

            // Restore the steady-state cloexec invariant on the fds we shed. (No handoff env var was set on
            // THIS process — it only lived in the execve envp — so a Restart=always bounce naturally takes
            // the normal restore path / Option 1, with no stale-fd adopt to guard against.)
            foreach (int fd in shedFds)
                ReExec.SetCloexec(fd);

            Console.Out.Flush();
            Console.Error.Flush();

            // RECOMMENDED recovery: bounce rather than limp. We do not release the gate first — the process
            // is about to die; the OS reclaims everything.
            Environment.Exit(70);
            return false; // unreachable; satisfies the compiler
        }
        catch (Exception ex)
        {
            // A throw before/around the exec (e.g. serialization) — same recovery posture: restore the
            // invariant and bounce, never limp with released handles. (The handoff only ever lived in the
            // execve envp, not this process's environment, so there is nothing to unset.)
            logger.LogCritical(ex,
                "hot-swap: produce/exec threw before replacing the image; exiting {Code} for a clean Restart=always recovery", 70);
            foreach (int fd in shedFds)
                ReExec.SetCloexec(fd);
            Console.Out.Flush();
            Console.Error.Flush();
            Environment.Exit(70);
            return false; // unreachable
        }
    }

    /// <summary>
    /// In the NEW image at boot, adopt every instance the predecessor handed off across the
    /// <c>execv</c> — the no-EOF resume. Reads the base64'd handoff from
    /// <see cref="HotSwapHandoff.EnvVarName"/>; absent → no-op (a plain start, not a swap). For each entry,
    /// if the inherited FIFO fd is still open it builds an adopted <see cref="RunningInstance"/> directly
    /// over THAT fd (NOT <c>ReopenFifo</c> — the continuously-open fd means the game never saw its writer
    /// vanish, so a post-swap <c>/quit</c> is honored immediately), recovers the PID from the cgroup, and
    /// restores counters/phase/timing/intent; an unexpectedly-invalid fd falls back to the Option-1 re-open
    /// for that one instance, logged as a downgrade. After the loop the env var is UNSET so a later plain
    /// restart never re-adopts stale fds. Runs FIRST in <see cref="StartupRestorer"/>, so the subsequent
    /// <see cref="RestoreAsync"/>/<see cref="AdoptLiveOrphansAsync"/> skip these via their
    /// <c>_instances.ContainsKey</c> guards.
    /// </summary>
    public void AdoptFromHandoff(CancellationToken ct = default)
    {
        string? blob = Environment.GetEnvironmentVariable(HotSwapHandoff.EnvVarName);
        if (string.IsNullOrEmpty(blob))
            return; // not a hot-swap boot — nothing handed off

        HotSwapHandoff? handoff;
        try
        {
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(blob));
            handoff = JsonSerializer.Deserialize(json, WatchdogJsonContext.Default.HotSwapHandoff);
        }
        catch (Exception ex)
        {
            // A malformed handoff must not wedge boot: log loudly and let the normal restore take over (the
            // FIFO nodes survive on disk, so Option-1 adoption still recovers the channel). Unset it so the
            // bad blob can't be re-read.
            logger.LogError(ex,
                "hot-swap: could not decode the handoff blob — falling back to the normal restore (Option 1)");
            Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, null);
            return;
        }

        if (handoff is null)
        {
            Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, null);
            return;
        }

        // Restore the player session maps FIRST, and independently of everything below: they are not tied
        // to the instance table (the handoff carries a map for every instance the ingester tracks, not only
        // those with a FIFO fd), and they matter even on the paths that adopt nothing — a supervisor that
        // came up not-ready still tails logs, and a leave line landing on an empty map is a presence event
        // lost for good.
        RestorePlayerSessions(handoff);

        if (handoff.Instances.Count == 0)
        {
            Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, null);
            return;
        }

        if (!state.Ready)
        {
            // The slice bootstrap failed in the successor; we cannot supervise. Leave the env var unset so
            // we don't retry on a later restart, and let the normal restore log the not-ready reason.
            logger.LogError(
                "hot-swap: supervisor not ready ({Detail}); cannot adopt {Count} handed-off instance(s)",
                state.Detail, handoff.Instances.Count);
            Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, null);
            return;
        }

        _gate.Wait(ct);
        try
        {
            var now = DateTime.UtcNow;
            int adopted = 0, downgraded = 0;

            foreach (var entry in handoff.Instances)
            {
                if (_instances.ContainsKey(entry.Name))
                    continue; // defensive: already tracked (shouldn't happen — this runs first)

                Instance? spec = null;
                try { spec = instances.GetInstanceInfo(entry.Name); }
                catch (Exception ex) { logger.LogWarning(ex, "hot-swap: kgsm-lib threw reading {Instance}", entry.Name); }

                if (spec is null)
                {
                    // The spec vanished while we swapped. We still hold an inherited fd, but with no spec we
                    // can't build a faithful handle (stop command/timeout unknown). Close the orphaned fd if
                    // it's valid (we own it now) and skip — the cgroup-only orphan adopt will pick it up.
                    if (ReExec.IsValidFd(entry.FifoFd))
                        NativeMethods.close(entry.FifoFd);
                    logger.LogWarning(
                        "hot-swap: {Instance} was handed off but kgsm-lib now returns no config — skipping (closed inherited fd)",
                        entry.Name);
                    continue;
                }

                if (AdoptHandoffEntry(entry, spec, now))
                    adopted++;
                else
                    downgraded++;
            }

            logger.LogInformation(
                "hot-swap: adopted {Adopted} instance(s) via inherited fds, {Downgraded} downgraded to FIFO re-open (of {Total} handed off)",
                adopted, downgraded, handoff.Instances.Count);
        }
        finally
        {
            // ALWAYS unset, even on a throw mid-loop: a later plain restart must never re-adopt stale fds.
            Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, null);
            _gate.Release();
        }
    }

    /// <summary>
    /// Adopt one handed-off entry. Returns true if it adopted the INHERITED fd directly (the seamless
    /// path), false if it had to downgrade to an Option-1 FIFO re-open (or cgroup-only) because the
    /// inherited fd unexpectedly did not survive. Pure-ish decision around the fd validity check; the
    /// caller holds the gate.
    /// </summary>
    private bool AdoptHandoffEntry(HotSwapEntry entry, Instance spec, DateTime now)
    {
        var si = new SupervisedInstance
        {
            Name = entry.Name,
            Spec = spec,
            DesiredRunning = entry.DesiredRunning,
            SpawnedAt = entry.SpawnedAt ?? now,
            NextRestartAt = entry.NextRestartAt,
        };
        // Restore phase (forward-compat: an unknown name leaves the default Running) and counters.
        if (Enum.TryParse<SupervisionPhase>(entry.Phase, ignoreCase: false, out var phase))
            si.Phase = phase;
        si.Restart.Restore(entry.ConsecutiveFailures, entry.GaveUp);

        int? pid = cgroups.FirstPid(entry.Name);

        // This game came across the execve as OUR child (same-PID swap) and we hold no managed Process for
        // it — so WE are now responsible for reaping it when it exits. Track its pid for the reaper. (True
        // for both fd paths below: the fd state doesn't change the process parentage.)
        if (pid is int adoptedPid && adoptedPid > 0)
            _reapable[adoptedPid] = 0;

        bool inheritedFdValid = ReExec.IsValidFd(entry.FifoFd);
        if (inheritedFdValid)
        {
            // The seamless path: adopt the SAME continuously-open fd the predecessor held. No ReopenFifo —
            // the game never saw its stdin writer vanish, so a post-swap /quit is honored immediately.
            si.Current = RunningInstance.Adopt(
                entry.Name, entry.FifoFd, entry.FifoPath, spec.StopCommand, spec.StopCommandTimeoutSeconds, pid, logger);
            // Restore the steady-state invariant on the adopted fd — it was shed for the exec only.
            ReExec.SetCloexec(entry.FifoFd);
            si.LastReason = "adopted via hot-swap handoff — console+graceful stop continuous, no EOF";
            _instances[entry.Name] = si;
            logger.LogInformation(
                "hot-swap: adopted {Instance} via inherited fd {Fd} (pid {Pid}) — console+graceful stop continuous, no EOF",
                entry.Name, entry.FifoFd, pid);
            return true;
        }

        // Downgrade: the inherited fd is gone (unexpected — it should have survived). Fall back to Option 1
        // (re-open the surviving FIFO node from disk); if even that fails, cgroup-only until the next respawn.
        int fd = spawnEngine.ReopenFifo(spec.SocketFile);
        if (fd >= 0)
        {
            si.Current = RunningInstance.Adopt(
                entry.Name, fd, spec.SocketFile, spec.StopCommand, spec.StopCommandTimeoutSeconds, pid, logger);
            si.LastReason = "adopted via hot-swap handoff (inherited fd gone — FIFO re-opened; brief EOF window possible)";
            logger.LogWarning(
                "hot-swap: {Instance} inherited fd {Fd} did not survive — DOWNGRADED to FIFO re-open (Option 1)",
                entry.Name, entry.FifoFd);
        }
        else
        {
            si.LastReason = "adopted via hot-swap handoff (inherited fd gone, FIFO re-open failed — cgroup-only until respawn)";
            logger.LogWarning(
                "hot-swap: {Instance} inherited fd {Fd} did not survive AND FIFO re-open failed — cgroup-only supervision",
                entry.Name, entry.FifoFd);
        }
        _instances[entry.Name] = si;
        return false;
    }

    /// <summary>
    /// Reap any zombie left behind by a hot-swap-adopted instance that has exited. Such a game survived a
    /// same-PID <c>execve</c> as our child, but the re-exec'd image holds no managed <c>Process</c> for it,
    /// so the runtime never reaps it — without this it lingers as a zombie until the daemon next restarts.
    /// Driven by the 1 Hz <see cref="CrashWatcher"/> tick. Lock-free and gate-free: it only touches
    /// <c>_reapable</c> (concurrent) and the <c>waitpid</c> syscall, never the supervision table. It targets
    /// ONLY the specific adopted pids — never <c>waitpid(-1)</c> — so it cannot reap a CLR-owned child and
    /// corrupt its exit code. <c>waitpid(WNOHANG)</c> never blocks and never kills: a live child returns 0
    /// (kept for next tick); a reaped or never-ours child drops out of the set.
    /// </summary>
    public void ReapOrphanedChildren()
    {
        if (_reapable.IsEmpty)
            return;

        foreach (var pid in _reapable.Keys)
        {
            int result = NativeMethods.waitpid(pid, out _, NativeMethods.WNOHANG);
            if (ShouldStopReaping(result, pid))
            {
                _reapable.TryRemove(pid, out _);
                if (result == pid)
                    logger.LogDebug("reaped exited hot-swap-adopted child pid {Pid}", pid);
            }
        }
    }

    /// <summary>
    /// Pure decision for <see cref="ReapOrphanedChildren"/>: stop tracking a pid once <c>waitpid</c> reports
    /// it reaped (<paramref name="waitpidResult"/> == <paramref name="pid"/>) or that it is not/no longer our
    /// child (a negative result, e.g. ECHILD). A zero result means "still alive" — keep tracking it.
    /// </summary>
    internal static bool ShouldStopReaping(int waitpidResult, int pid) => waitpidResult == pid || waitpidResult < 0;

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

    /// <summary>
    /// The instances the reconcile sweep is answerable for: running right now, and opted into port
    /// forwarding. Lock-free over the same instance table <see cref="Status"/> reads, so a sweep never
    /// contends with a lifecycle verb.
    /// <para>
    /// Liveness is measured (<c>cgroup.events</c>), not inferred from the phase alone — an instance
    /// between a crash and its respawn holds no ports the router should be forwarding to, and a sweep
    /// that re-opened for it would be asserting a mapping for nothing.
    /// </para>
    /// </summary>
    public ForwardingCandidate[] ForwardingCandidates() =>
        [.. _instances.Values
            .Where(si => si.DesiredRunning
                         && si.Spec.EnablePortForwarding
                         && si.Spec.Ports.Count > 0
                         && cgroups.IsPopulated(si.Name))
            .Select(si => new ForwardingCandidate(si.Name, si.Spec))];

    /// <inheritdoc />
    public IReadOnlySet<(int Port, string Protocol)> ForwardedPortsHeldByOthers(string excluding)
    {
        var held = new HashSet<(int Port, string Protocol)>();
        foreach (SupervisedInstance si in _instances.Values)
        {
            if (string.Equals(si.Name, excluding, StringComparison.Ordinal))
                continue;
            if (!si.DesiredRunning || !si.Spec.EnablePortForwarding)
                continue;

            foreach (var (port, protocol) in si.Spec.Ports.Expand())
                held.Add((port, protocol.ToLowerInvariant()));
        }

        return held;
    }

    /// <summary>
    /// Whether an instance is still running <em>right now</em> — the sweep's re-check after a re-assert.
    /// A stop can land while upnpc is mid-call, and a forward left behind for something that is no longer
    /// running is precisely the state the open-on-start/close-on-stop lifetime exists to prevent.
    /// </summary>
    public bool IsRunning(string name) =>
        _instances.TryGetValue(name, out var si) && si.DesiredRunning && cgroups.IsPopulated(name);

    /// <summary>
    /// Record that the sweep put back router forwards the IGD had dropped while the instance kept
    /// running. Separate from <see cref="OpenPortForwarding"/>'s event because the two are different
    /// facts — an open accompanies a bring-up, this one says the mapping went missing with nothing on
    /// this host asking for it — and it reports only <paramref name="restored"/>, the subset that was
    /// actually missing, never the instance's whole configured set.
    /// </summary>
    public void NoteUpnpReasserted(string name, IReadOnlyList<PortMapping> restored) =>
        journal.Ports("instance-upnp-reasserted", name, restored);

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
        // INTENT GATES DETECTION. A record with DesiredRunning=false is one an operator asked to stop,
        // so its exit is the stop we asked for — never a crash, whatever the exit code says (a hard
        // teardown exits 137/SIGKILL by construction). Checked BEFORE the phase dispatch so it covers
        // both a Running record whose process just went away and a RestartPending one whose restart was
        // queued before the stop landed.
        if (!si.DesiredRunning)
        {
            ReconcileStopIntent(si);
            return;
        }

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

    /// <summary>
    /// Finish a stop the control verb began but did not complete. <see cref="StopAsync"/> flips
    /// <see cref="SupervisedInstance.DesiredRunning"/> to false as its first act and removes the table
    /// entry as its last, so a record still here with intent=stopped means the verb unwound in between —
    /// an exception, or (before the stop path was made non-cancellable) a caller that hung up mid-drain.
    /// Left alone such a record keeps <c>Phase=Running</c> over a dead cgroup, which the crash path then
    /// reads as a crash and restarts: the operator's stop silently becomes a start.
    /// <para>
    /// The exit is deliberately unclassified — no exit-code read, no policy, no retry slot, no
    /// <c>instance-crashed</c> event. The instance is torn down, marked <c>Stopped</c>, and dropped from
    /// the table exactly as a completed stop would leave it. A still-populated cgroup is left strictly
    /// alone: the drain may still be in flight, and reconcile has no business hard-killing on a deadline
    /// it did not set — the record simply stays visible (desired=stopped, running=true) until the process
    /// exits or an operator re-issues the stop.
    /// </para>
    /// </summary>
    private void ReconcileStopIntent(SupervisedInstance si)
    {
        if (cgroups.IsPopulated(si.Name))
            return; // still draining — nothing to conclude yet

        DisposeCurrent(si);
        PurgeCgroup(si.Name);
        ForgetPlayerSessions(si.Name);
        si.Phase = SupervisionPhase.Stopped;
        si.LastReason = "stopped (stop request completed after the caller abandoned it)";
        _instances.TryRemove(si.Name, out _);
        PersistSupervisionState();
        RecordRunEnd(si, RunRecord.Stopped, exit: null);

        logger.LogInformation(
            "{Instance} exited under a pending stop — completing the teardown, not restarting (intent: stopped)", si.Name);
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
                PersistSupervisionState(); // the streak just reset — persist so a daemon death keeps the truth
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
        // The process is gone, so every player it was serving is disconnected — whatever the policy
        // below decides about restarting. Cleared here, once, ahead of the crash/failed/stay-down
        // branches so none of them can leave the map describing a dead process.
        ForgetPlayerSessions(name);

        string exitText = exit is int c ? $"exit {c}" : "exit unknown";

        // Default policy (Always) restarts on any exit while desired-running — the only "stay down" is
        // a deliberate /stop (which carries the operator's intent far more reliably than an exit code,
        // since game servers routinely exit 0 even on a fatal error). Opt-in on-failure instead leaves
        // a clean (code 0) exit stopped — see RestartPolicyMode for that footgun.
        bool autoRestart = si.Spec.CrashRestart ?? true;
        var effectivePolicy = EffectivePolicyFor(si);

        // crashRestart=false: operator explicitly disabled auto-recovery. Emit EventCrashed for alert
        // visibility, then treat as Stopped (no retry slot consumed — the streak stays where it was).
        if (!autoRestart)
        {
            si.Phase = SupervisionPhase.Stopped;
            si.LastReason = $"exited ({exitText}); not restarted (crash_restart=false)";
            logger.LogInformation("{Instance} exited ({Exit}); not restarting (crash_restart=false policy)", name, exitText);
            PersistSupervisionState();
            RecordRunEnd(si, RunRecord.Crashed, exit);
            journal.Supervision(EventCrashed, name, exit, 0);
            return;
        }

        if (!effectivePolicy.ShouldRestartAfter(exit))
        {
            si.Phase = SupervisionPhase.Stopped;
            si.LastReason = "exited cleanly (code 0); not restarted (on-failure policy)";
            logger.LogInformation("{Instance} exited cleanly; not restarting (on-failure policy)", name);
            PersistSupervisionState(); // clean exit → Stopped (terminal); persist so it isn't resurrected as Running
            RecordRunEnd(si, RunRecord.Exited, exit);
            return;
        }

        string verb = exit == 0 ? "exited cleanly" : "crashed";
        TimeSpan? delay = si.Restart.RegisterCrash(effectivePolicy);
        if (delay is null)
        {
            si.Phase = SupervisionPhase.Failed;
            si.LastReason = $"restart limit reached ({si.Restart.ConsecutiveFailures} consecutive failures, last {exitText}); gave up after {effectivePolicy.MaxRetries} retries";
            logger.LogWarning("{Instance} hit the restart limit ({Count} failures, last {Exit}); giving up after {Max} retries — reporting failed",
                name, si.Restart.ConsecutiveFailures, exitText, effectivePolicy.MaxRetries);
            PersistSupervisionState(); // gave up (→Failed) — persist the latch so an OOM doesn't fake recovery
            RecordRunEnd(si, RunRecord.GaveUp, exit);
            journal.Supervision(EventFailed, name, exit, si.Restart.ConsecutiveFailures);
            return;
        }

        si.Phase = SupervisionPhase.RestartPending;
        si.NextRestartAt = now + delay.Value;
        si.LastReason = $"{verb} ({exitText}); restart #{si.Restart.ConsecutiveFailures} in {(int)delay.Value.TotalSeconds}s";
        logger.LogWarning("{Instance} {Verb} ({Exit}); restart #{N} in {Delay}",
            name, verb, exitText, si.Restart.ConsecutiveFailures, delay.Value);
        PersistSupervisionState(); // crash registered (→RestartPending) — persist the streak across any daemon death
        // Recorded BEFORE the restart: the next spawn rotates this console away, and the ledger row is
        // what lets a consumer find it afterwards.
        RecordRunEnd(si, RunRecord.Crashed, exit);
        journal.Supervision(EventCrashed, name, exit, si.Restart.ConsecutiveFailures);
    }

    private void ReconcileRestartPending(SupervisedInstance si, DateTime now)
    {
        if (si.NextRestartAt is DateTime due && now < due)
            return; // still waiting out the backoff delay

        logger.LogInformation("{Instance} restarting (attempt #{N})", si.Name, si.Restart.ConsecutiveFailures);
        if (TrySpawn(si, now, out var reason))
        {
            si.LastReason = $"restarted (attempt #{si.Restart.ConsecutiveFailures}); {reason}";
            // The autonomous crash-recovery respawn succeeded — emit instance-restarted (system/system)
            // so the action is auditable downstream (kgsm-api maps it to server.restart and bridges the
            // alert's resolution.actionId). Emitted per successful attempt, symmetric with the per-crash
            // instance-crashed above; a respawn that immediately re-dies just re-fires the crash next tick.
            journal.Instance(EventRestarted, si.Name);
            return;
        }

        // The respawn itself failed to even start (e.g. a now-missing binary) — count it as a failure.
        TimeSpan? delay = si.Restart.RegisterCrash(policy);
        if (delay is null)
        {
            si.Phase = SupervisionPhase.Failed;
            si.LastReason = $"restart failed ({reason}); crash-looped ({si.Restart.ConsecutiveFailures} failures); gave up after {policy.MaxRetries} retries";
            logger.LogWarning("{Instance} restart failed and gave up after {Max} retries: {Reason}", si.Name, policy.MaxRetries, reason);
            PersistSupervisionState(); // gave up (→Failed) after a failed respawn — persist the latch
            // The respawn never produced a process, so there is no exit code to report (honest unknown).
            journal.Supervision(EventFailed, si.Name, null, si.Restart.ConsecutiveFailures);
            return;
        }
        si.NextRestartAt = now + delay.Value;
        si.LastReason = $"restart failed ({reason}); retry #{si.Restart.ConsecutiveFailures} in {(int)delay.Value.TotalSeconds}s";
        logger.LogWarning("{Instance} restart failed ({Reason}); retry #{N} in {Delay}",
            si.Name, reason, si.Restart.ConsecutiveFailures, delay.Value);
        PersistSupervisionState(); // another failed-respawn streak increment — persist it
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>
    /// The shared mechanics of every bring-up/teardown network side effect: run it off the supervisor
    /// thread so a slow router or an unreachable authority never delays a start result or holds the gate,
    /// emit the audit event ONLY when the side effect reports a confirmed change, and let nothing escape
    /// — a faulted side effect must not take down supervision.
    /// <para>
    /// Each caller passes its own gate-and-apply (the services self-gate on their per-instance config) and
    /// the dash event name for a confirmed transition. Extracted so the rules that are easy to get wrong —
    /// off-thread, emit-only-on-applied, swallow-and-log — live in one place rather than being restated by
    /// every side effect that hangs off these edges.
    /// </para>
    /// <para>
    /// <paramref name="appliedPorts"/> overrides the ports the event reports for a side effect that acts
    /// on less than the instance declares — the event must name what actually changed, and the declared
    /// set is only the right answer when the whole of it was applied.
    /// </para>
    /// </summary>
    private void FireAndForget(
        string what, Instance spec, Func<Task<bool>> apply, string appliedEvent,
        IReadOnlyList<PortMapping>? appliedPorts = null)
        => _ = Task.Run(async () =>
        {
            try
            {
                // The watchdog drove the change, so it records it — structured, straight from the
                // mappings it already holds.
                if (await apply().ConfigureAwait(false))
                    journal.Ports(appliedEvent, spec.Name, appliedPorts ?? spec.Ports);
            }
            catch (Exception ex) { logger.LogWarning(ex, "{What} task faulted for {Instance}", what, spec.Name); }
        });

    /// <summary>
    /// Runs a best-effort side operation that ANOTHER component records, off the supervision path.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="FireAndForget"/>, and the distinction is the whole of "emitter =
    /// author": that one is for an edge this daemon both performs and records, this one for an edge it
    /// merely asks another authority to perform. The outcome is deliberately unused — whether the
    /// firewall changed is the firewall's to report, and reading it here to decide whether to write a
    /// line is exactly the second author being removed.
    /// </remarks>
    private void AskAndForget(string what, Instance spec, Func<Task> ask)
        => _ = Task.Run(async () =>
        {
            try { await ask().ConfigureAwait(false); }
            catch (Exception ex) { logger.LogWarning(ex, "{What} task faulted for {Instance}", what, spec.Name); }
        });

    /// <summary>
    /// Best-effort UPnP open on a fresh bring-up (a manual <c>start</c> or a boot respawn of a dead
    /// instance — NOT a crash-restart, where the router lease is deliberately held). The service
    /// self-gates on <c>enable_port_forwarding</c> and time-boxes upnpc.
    /// </summary>
    private void OpenPortForwarding(Instance spec)
        => FireAndForget("UPnP open", spec,
            async () => await upnp.OpenAsync(spec).ConfigureAwait(false) == UpnpOutcome.Applied,
            "instance-upnp-opened");

    /// <summary>
    /// Best-effort UPnP close on a deliberate stop. Same posture as <see cref="OpenPortForwarding"/>;
    /// a confirmed removal emits the close event.
    /// <para>
    /// Only the ports no other instance still wants are released — a router forward is one row per
    /// (port, protocol) whoever declares it, so a stop that deleted its whole declared set would take a
    /// still-running sibling off the air (<see cref="IForwardedPortClaims"/>).
    /// </para>
    /// </summary>
    private void ClosePortForwarding(Instance spec)
    {
        // Read at the stop edge rather than inside the task: this instance's own intent is already
        // cleared by the time we get here, so what comes back is exactly what OTHER instances claim.
        IReadOnlySet<(int Port, string Protocol)> retain = ForwardedPortsHeldByOthers(spec.Name);

        // The event names what was actually released. A shared port that stays mapped for a sibling was
        // not closed, and reporting it as closed would put a transition that never happened into the
        // audit trail. Nothing retained means the declared set IS the released set, reported in its
        // original range form.
        IReadOnlyList<PortMapping> released = retain.Count == 0
            ? spec.Ports
            : [.. PortSets.Collapse(spec.Ports.Expand().Where(p => !retain.Holds(p.Port, p.Protocol)))];

        FireAndForget("UPnP close", spec,
            async () => await upnp.CloseAsync(spec, retain).ConfigureAwait(false) == UpnpOutcome.Applied,
            "instance-upnp-closed", released);
    }

    /// <summary>
    /// Best-effort host-firewall open on a fresh bring-up, the host-side peer of
    /// <see cref="OpenPortForwarding"/> — an instance's ports are open exactly while it runs. Fires on the
    /// same edges as UPnP and for the same reason: a crash does not drop a host rule and the respawn still
    /// needs it, so only a fresh bring-up opens and only an intended stop closes. The service self-gates
    /// on <c>enable_firewall_management</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Records nothing.</b> kgsm-firewall performs the change and writes the
    /// <c>instance_ports_opened</c> line to its own journal — this daemon asked, which is not the same
    /// as having done it. Journaling here as well would put one firewall edge in the trail twice, under
    /// two different authors.
    /// </remarks>
    private void OpenFirewallPorts(Instance spec)
        => AskAndForget("firewall open", spec,
            () => firewall.OpenAsync(spec, WatchdogJournal.ActorWatchdog, OriginSystem));

    /// <summary>
    /// Best-effort host-firewall close on a deliberate stop, releasing the ports the instance held.
    /// </summary>
    /// <remarks>⚠ Records nothing, for the same reason as <see cref="OpenFirewallPorts"/>.</remarks>
    private void CloseFirewallPorts(Instance spec)
        => AskAndForget("firewall close", spec,
            () => firewall.CloseAsync(spec, WatchdogJournal.ActorWatchdog, OriginSystem));

    // This daemon's own identity is WatchdogJournal's to state; only the origin prefix for a
    // requester-attributed actor is needed here.
    private const string OriginSystem = "system";

    /// <summary>
    /// The actor for an action another leaf asked this daemon to take: the requester, in the
    /// <c>provider:name</c> form every consumer parses. A requester that already names its identity
    /// source is passed through untouched; a bare leaf name (what every caller sends) is stamped
    /// <c>system:&lt;name&gt;</c>, because an unprefixed actor is read downstream as a person on the local
    /// host. A blank requester is this daemon — the honest fallback, never an invented name.
    /// </summary>
    private static string ProvenanceActor(string requester) =>
        string.IsNullOrWhiteSpace(requester) ? WatchdogJournal.ActorWatchdog
            : requester.Contains(':') ? requester
            : $"{OriginSystem}:{requester}";

    /// <summary>
    /// Snapshot every supervised instance's restart bookkeeping (streak, give-up latch, phase, timing,
    /// last reason) to disk via <see cref="SupervisionStateStore"/>, so it survives ANY daemon death.
    /// Called after each meaningful transition (crash → restart-pending, give-up → failed, stability
    /// reset, clean stop, deliberate stop/disable). The file is tiny and counters only move on a crash,
    /// so a full per-transition snapshot is cheap; the store's write is atomic + best-effort, so a failed
    /// write never breaks supervision. Always invoked under the gate (every caller holds it).
    /// </summary>
    private void PersistSupervisionState()
    {
        var state = new PersistedSupervisionState();
        foreach (var si in _instances.Values)
        {
            state.Instances[si.Name] = new InstanceRestartState
            {
                ConsecutiveFailures = si.Restart.ConsecutiveFailures,
                GaveUp = si.Restart.GaveUp,
                Phase = si.Phase.ToString(),
                SpawnedAt = si.SpawnedAt,
                NextRestartAt = si.NextRestartAt,
                LastReason = si.LastReason,
            };
        }
        supervisionStore.Save(state);
    }

    /// <summary>
    /// Record how a run ended in the <see cref="RunHistoryStore"/> ledger, so a consumer correlating a
    /// crash against console output can find the run that holds it instead of inferring one from
    /// timestamps. Called from each transition that concludes a run, alongside
    /// <see cref="PersistSupervisionState"/>, and always under the gate.
    /// </summary>
    /// <remarks>
    /// <b>The end time is the console file's, not the clock's.</b> A run's identity is the last line it
    /// printed; the moment the supervisor noticed the cgroup had emptied is a different quantity,
    /// arriving up to a poll interval later, and a ledger stamped with it would not match the file it
    /// describes. Reading mtime here is what makes the join exact — rotation moves the file with
    /// <c>rename(2)</c>, which does not touch mtime, so the same value still identifies it afterwards.
    /// <para>
    /// An instance whose console does not exist or is empty produces no row. There is no run to
    /// describe, and a row that cannot be joined to a file is worse than none: it would occupy a
    /// bounded slot while answering nothing.
    /// </para>
    /// </remarks>
    private void RecordRunEnd(SupervisedInstance si, string outcome, int? exit)
    {
        string log = si.Spec.LogFile;
        if (string.IsNullOrEmpty(log))
            return;

        DateTime endedAt;
        try
        {
            var info = new FileInfo(log);
            if (!info.Exists || info.Length == 0)
                return;
            endedAt = info.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not read {Log} to date {Instance}'s run; not recording it", log, si.Name);
            return;
        }

        runHistory.Record(si.Name, new RunRecord(
            EndedAt: endedAt,
            StartedAt: si.SpawnedAt,
            Outcome: outcome,
            ExitCode: exit,
            RestartCount: si.Restart.ConsecutiveFailures,
            Detail: si.LastReason));
    }

    /// <summary>
    /// The effective <see cref="BackoffPolicy"/> for one instance: the global policy with
    /// <see cref="BackoffPolicy.MaxRetries"/> overridden by the instance's <c>crash_max_restarts</c>
    /// config key when present (null → keep the global default). Pure — no side effects.
    /// </summary>
    private BackoffPolicy EffectivePolicyFor(SupervisedInstance si) =>
        si.Spec.CrashMaxRestarts is int maxR
            ? new BackoffPolicy
            {
                BaseDelay = policy.BaseDelay,
                MaxDelay = policy.MaxDelay,
                MaxRetries = maxR,
                StabilityThreshold = policy.StabilityThreshold,
                GraceWindow = policy.GraceWindow,
                Mode = policy.Mode,
            }
            : policy;

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

    /// <summary>
    /// Re-seed the player session maps handed across a hot-swap. A session with no key is dropped: the
    /// key is what a later leave line resolves against, so an entry without one could never be matched
    /// and would only inflate what <c>GET /players</c> reports.
    /// </summary>
    private void RestorePlayerSessions(HotSwapHandoff handoff)
    {
        int restored = 0;
        foreach (var kvp in handoff.PlayerSessions)
        {
            var usable = kvp.Value
                .Where(s => !string.IsNullOrEmpty(s.SessionKey))
                .Select(s => (SessionKey: s.SessionKey!, s.Id, s.Name, s.Addr))
                .ToArray();

            if (usable.Length == 0)
                continue;

            sessions.Restore(kvp.Key, usable);
            restored += usable.Length;
        }

        if (restored > 0)
            logger.LogInformation("hot-swap: restored {Count} player session(s) across {Instances} instance(s)",
                restored, handoff.PlayerSessions.Count);
    }

    /// <summary>
    /// Drop every player session tracked for an instance whose process has ended.
    /// </summary>
    /// <remarks>
    /// A session map describes connections to a running process; a process that has exited holds
    /// none. Left standing, the map answers <c>GET /players</c> with players who cannot be connected
    /// for as long as the instance stays down — and a consumer that reconciles its own roster from
    /// that snapshot (kgsm-api does, on every restart) writes the phantom into its permanent record.
    /// The ingesters clear the map on their own when the log rolls to a fresh session, which is the
    /// NEXT start; the process ending is the earlier and more truthful edge, so it is cleared here,
    /// at the one place every teardown passes through.
    /// <para>
    /// Emits nothing. A stop is one fact, already carried by the lifecycle event; a
    /// <c>instance-player-left</c> per tracked session would mint leave records the game never
    /// reported, and consumers reset a server's roster wholesale on that lifecycle event anyway.
    /// </para>
    /// </remarks>
    private void ForgetPlayerSessions(string name)
    {
        if (!sessions.HasSessions(name))
            return;

        sessions.Reset(name);
        logger.LogDebug("player session map cleared for {Instance} (process ended)", name);
    }

    private async Task<bool> WaitForPopulatedAsync(string name, RunningInstance ri, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cgroups.IsPopulated(name))
                return true;
            // If the launcher died before populating, stop waiting — the spawn failed. (ri is a freshly
            // spawned handle here, so Process is non-null; the ?. keeps the nullable contract honest.)
            try { if (ri.Process?.HasExited == true) return cgroups.IsPopulated(name); }
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
        // Daemon shutdown — the games keep running. RELEASE each handle (close our fd) but KEEP the FIFO
        // node, so the next daemon can re-open it on adoption and restore console + graceful stop. Calling
        // Dispose() here (the old behaviour) deleted live games' stdin FIFOs, orphaning the channel.
        foreach (var si in _instances.Values)
            si.Current?.ReleaseKeepingFifo();
        _gate.Dispose();
    }
}
