using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The registry and orchestration the control surface drives. Holds the desired-state table
/// (which instances the daemon was told to run) and the live <see cref="RunningInstance"/> handles.
/// Increment 1 covers <c>start</c>/<c>stop</c>/<c>status</c>/<c>list</c>; crash detection + restart
/// (the <c>cgroup.events</c> watch that reads this same desired state) is Increment 2.
/// <para>
/// Reads (status/list) are lock-free over a <see cref="ConcurrentDictionary{TKey,TValue}"/>; the two
/// mutating verbs are serialized through one gate so a long graceful-stop drain can't interleave
/// with a start of the same instance. Throughput here is a handful of ops, so a single gate is the
/// right simplicity.
/// </para>
/// </summary>
internal sealed class InstanceSupervisor(
    IInstanceService instances,
    SpawnEngine spawnEngine,
    CgroupManager cgroups,
    SupervisorState state,
    ILogger<InstanceSupervisor> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, RunningInstance> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ActionResult> StartAsync(string name, CancellationToken ct = default)
    {
        if (!state.Ready)
            return new ActionResult(name, false, $"supervisor not ready: {state.Detail}");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_running.TryGetValue(name, out var existing))
            {
                if (cgroups.IsPopulated(name))
                    return new ActionResult(name, true, "already running");
                // Tracked but dead (crashed before Inc 2's watcher exists): clean up and re-spawn.
                Cleanup(existing, killCgroup: true);
            }

            var instance = instances.GetInstanceInfo(name);
            if (instance is null)
                return new ActionResult(name, false, "unknown instance (kgsm-lib returned no info)");

            // Scope guard (PLAN §8): the watchdog supervises NATIVE STANDALONE instances. systemd
            // instances are supervised by systemd; container instances by Docker. No-op on those.
            if (instance.Runtime != InstanceRuntime.Native || instance.LifecycleManager != LifecycleManager.Standalone)
                return new ActionResult(name, false,
                    $"out of scope: {instance.Runtime}/{instance.LifecycleManager} — the watchdog only supervises native standalone instances");

            RunningInstance ri;
            try
            {
                ri = spawnEngine.Spawn(instance);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "spawn failed for {Instance}", name);
                return new ActionResult(name, false, $"spawn failed: {ex.Message}");
            }

            // Confirm the game actually entered its cgroup (the launcher's self-move succeeded and
            // the process is alive). Bounded; never blocks indefinitely.
            bool populated = await WaitForPopulatedAsync(name, ri, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (!populated)
            {
                Cleanup(ri, killCgroup: true);
                return new ActionResult(name, false,
                    "process did not enter its cgroup (exited immediately or self-move failed; check the instance log)");
            }

            _running[name] = ri;
            logger.LogInformation("started {Instance} (pid {Pid})", name, ri.Pid);
            return new ActionResult(name, true, $"started (pid {ri.Pid})");
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
            if (!_running.TryGetValue(name, out var ri))
            {
                // Not tracked — but a previous daemon (or a crash window) may have left a live
                // cgroup. If so, tear it down; otherwise it's simply not running.
                if (cgroups.IsPopulated(name))
                {
                    cgroups.Kill(name);
                    await WaitForDrainAsync(name, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    cgroups.Remove(name);
                    return new ActionResult(name, true, "stopped (untracked live cgroup torn down)");
                }
                return new ActionResult(name, true, "not running");
            }

            ri.DesiredRunning = false;

            // Graceful: write the stop command into the FIFO, then drain up to the instance's timeout.
            if (!string.IsNullOrEmpty(ri.StopCommand))
            {
                logger.LogInformation("stopping {Instance}: sending stop command", name);
                ri.SendLine(ri.StopCommand);
            }

            var timeout = TimeSpan.FromSeconds(Math.Max(1, ri.StopTimeoutSeconds));
            bool drained = await WaitForDrainAsync(name, timeout, ct).ConfigureAwait(false);

            if (!drained)
            {
                // Hard teardown: atomic whole-tree SIGKILL — no pgrep -P, nothing escapes.
                logger.LogWarning("{Instance} did not stop gracefully in {Seconds}s; cgroup.kill", name, timeout.TotalSeconds);
                cgroups.Kill(name);
                await WaitForDrainAsync(name, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }

            cgroups.Remove(name);
            Cleanup(ri, killCgroup: false);
            _running.TryRemove(name, out _);

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
        if (_running.TryGetValue(name, out var ri))
            return new InstanceState(
                name,
                ri.DesiredRunning ? "running" : "stopped",
                cgroups.IsPopulated(name),
                ri.Pid,
                cgroups.PathFor(name));

        // Untracked but possibly alive (e.g. orphaned across a daemon restart — re-adoption is Inc 2/3).
        if (cgroups.IsPopulated(name))
            return new InstanceState(name, "unknown", true, null, cgroups.PathFor(name));

        return null;
    }

    public InstanceState[] List()
    {
        return _running.Values
            .Select(ri => new InstanceState(
                ri.Name,
                ri.DesiredRunning ? "running" : "stopped",
                cgroups.IsPopulated(ri.Name),
                ri.Pid,
                cgroups.PathFor(ri.Name)))
            .ToArray();
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

    private void Cleanup(RunningInstance ri, bool killCgroup)
    {
        if (killCgroup)
        {
            cgroups.Kill(ri.Name);
            cgroups.Remove(ri.Name);
        }
        ri.Dispose();
        _running.TryRemove(ri.Name, out _);
    }

    public void Dispose()
    {
        foreach (var ri in _running.Values)
            ri.Dispose();
        _gate.Dispose();
    }
}
