using Microsoft.Extensions.Logging;

namespace TheKrystalShip.KGSM.Watchdog.Cgroup;

/// <summary>
/// Boot-time cgroup setup under <b>systemd delegation</b> (PLAN §4 / Increment 8). The unit runs the
/// daemon as the KGSM user inside a delegated cgroup subtree (<c>User=kgsm</c>, <c>Slice=kgsm.slice</c>,
/// <c>Delegate=yes</c>): systemd creates <c>kgsm.slice/kgsm-watchdog.service</c>, enables the controllers
/// on the parent slice, and chowns that service subtree to the user. The daemon therefore needs <b>no
/// root step and no privilege drop</b> — it only:
/// <list type="number">
/// <item>discovers its own delegated base from <c>/proc/self/cgroup</c> (the service cgroup, or its
///   parent when an Inc-7 hot-swap re-exec has already moved it into the supervisor leaf);</item>
/// <item>creates the supervisor leaf and moves itself into it — cgroup v2 forbids enabling
///   <c>subtree_control</c> on a cgroup that holds processes, and the daemon is born in the base;</item>
/// <item>enables the controllers on the (now process-free) base so each per-instance child
///   (<c>&lt;base&gt;/&lt;inst&gt;</c>) inherits them.</item>
/// </list>
/// <para>
/// Per-instance cgroups are children of the delegated <b>service</b> cgroup, never siblings of it under
/// the slice. systemd reconciles a slice's own <c>cgroup.subtree_control</c> on every <c>daemon-reload</c>
/// (stripping the controllers off any sibling cgroups it does not manage — the root cause of per-server
/// memory reading 0), but it leaves the delegated subtree below the service untouched. This is the fix.
/// </para>
/// <para>
/// A failed bootstrap never crashes the daemon: it sets <see cref="SupervisorState.Ready"/>=false with a
/// precise reason, so the control plane stays diagnosable and <c>/start</c> reports why.
/// </para>
/// </summary>
internal sealed class CgroupBootstrap(
    WatchdogOptions options,
    CgroupManager cgroups,
    SupervisorState state,
    ILogger<CgroupBootstrap> logger)
{
    public void Run()
    {
        try
        {
            // 1. Discover the delegated base systemd handed us. Authoritative over the configured
            //    CgroupBaseName, which stays only as a fallback when discovery fails.
            string? discovered = CgroupDiscovery.ResolveDelegatedBase(
                options.CgroupMountPoint, options.SupervisorLeaf);
            if (discovered is not null)
            {
                cgroups.UseResolvedBase(discovered);
                logger.LogInformation(
                    "delegated cgroup base resolved to {Base} (per-instance cgroups at {Base}/<instance>); " +
                    "kgsm must surface the matching cgroup_path — keep kgsm config_cgroup_base_name = '{Rel}'.",
                    discovered, discovered, RelativeTo(options.CgroupMountPoint, discovered));
            }
            else
            {
                logger.LogWarning(
                    "could not read /proc/self/cgroup; falling back to configured base {Base}", cgroups.Base);
            }

            // 2. The base must be a writable, delegated cgroup (systemd Delegate=yes chowns it to us).
            //    No root-boot fallback any more — the host is systemd-only (PLAN Inc 8).
            if (!cgroups.Supported())
            {
                state.Ready = false;
                state.Detail =
                    $"{cgroups.Base} is not a writable delegated cgroup — launch under systemd with " +
                    "User=<kgsm>, Slice=kgsm.slice, Delegate=yes (the daemon no longer self-bootstraps as root).";
                logger.LogError("{Detail}", state.Detail);
                return;
            }

            // 3. Create the supervisor leaf and move ourselves into it BEFORE enabling controllers:
            //    cgroup v2 forbids enabling subtree_control on a cgroup that holds processes, and the
            //    daemon is born in the base (the service cgroup). Writing our own PID to a cgroup we
            //    are already in (hot-swap re-exec) is a harmless no-op.
            Directory.CreateDirectory(cgroups.SupervisorPath);
            if (!cgroups.AttachToDir(cgroups.SupervisorPath, Environment.ProcessId))
            {
                state.Ready = false;
                state.Detail = $"could not enter supervisor leaf {cgroups.SupervisorPath}";
                logger.LogError("{Detail}", state.Detail);
                return;
            }

            // 4. Enable controllers on the (now process-free) base so per-instance children inherit
            //    them. Idempotent; once enabled on a delegated subtree they persist across daemon-reload
            //    (unlike the slice's own subtree_control), so this is also cheap re-assert insurance.
            //    Caveat: on a fresh service START (cold restart/crash, NOT a reload/hot-swap) systemd
            //    first clears this subtree_control, so a game that SURVIVED the restart (KillMode=process,
            //    re-adopted) is momentarily detached from the memory controller and re-charges from zero
            //    here — cgroup v2 does not retroactively charge already-resident pages. Inherent; it
            //    self-heals when that game next restarts. Fresh spawns and host reboots are unaffected.
            if (!cgroups.EnableControllers(cgroups.Base))
                logger.LogWarning("could not enable controllers on delegated base {Base}", cgroups.Base);

            state.Ready = true;
            state.Detail = $"delegated; base {cgroups.Base}, in {cgroups.SupervisorPath}";
            logger.LogInformation("bootstrap complete (systemd delegation): {Detail}", state.Detail);
        }
        catch (Exception ex)
        {
            state.Ready = false;
            state.Detail = $"bootstrap threw: {ex.Message}";
            logger.LogError(ex, "bootstrap failed");
        }
    }

    /// <summary>The base path relative to the cgroup mount, for the operator hint (kgsm config coupling).</summary>
    private static string RelativeTo(string mount, string abs)
        => abs.StartsWith(mount, StringComparison.Ordinal) ? abs[mount.Length..].TrimStart('/') : abs;
}
