using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Watchdog.Interop;

namespace TheKrystalShip.KGSM.Watchdog.Cgroup;

/// <summary>
/// The boot-time, load-bearing privilege sequence (PLAN §4). Runs once before the control
/// socket binds. Two entry contexts, one goal — the daemon ends up living inside
/// <c>kgsm.slice/&lt;supervisor&gt;</c> so it can fork games that are born in-slice and move them to
/// per-instance cgroups with no further privilege:
/// <list type="bullet">
/// <item><b>Booted as root</b> (init script or root systemd unit): create + delegate the slice
/// (subsuming <c>kgsm system setup-cgroups</c>), enter the supervisor leaf, then <b>drop to the
/// target user</b>. A uid change does not change cgroup membership, so the daemon stays in-slice.</item>
/// <item><b>Already delegated</b> (systemd <c>User=kgsm, Slice=kgsm.slice, Delegate=yes</c>, or a
/// manual user scope): the base is writable and the daemon is already inside the slice → just
/// ensure the supervisor leaf and move into it; no root step.</item>
/// </list>
/// The privilege drop order is deliberate and must not be reordered: <c>setgroups</c> →
/// <c>setresgid</c> → <c>setresuid</c>. Dropping the uid first would forfeit the privilege needed
/// to drop the gids and supplementary groups, leaving root group membership behind.
/// </summary>
internal sealed class CgroupBootstrap(
    WatchdogOptions options,
    CgroupManager cgroups,
    SupervisorState state,
    ILogger<CgroupBootstrap> logger)
{
    private const uint Unchanged = 0xFFFFFFFF;

    public void Run()
    {
        bool isRoot = NativeMethods.geteuid() == 0;
        logger.LogInformation(
            "bootstrap: euid-root={Root}, base={Base}, supervisor={Sup}",
            isRoot, cgroups.Base, cgroups.SupervisorPath);

        try
        {
            if (isRoot)
                RunAsRoot();
            else
                RunDelegated();
        }
        catch (Exception ex)
        {
            state.Ready = false;
            state.Detail = $"bootstrap threw: {ex.Message}";
            logger.LogError(ex, "bootstrap failed");
        }
    }

    // ---- root path: delegate, enter, drop --------------------------------------------------

    private void RunAsRoot()
    {
        // 1. Make controllers available to the base by enabling them at the mount root first
        //    (best-effort: on a systemd host they are already enabled and this is a no-op).
        if (!cgroups.EnableControllers(options.CgroupMountPoint))
            logger.LogWarning("could not enable controllers at mount root (often fine on systemd hosts)");

        // 2. Create KGSM's base and enable controllers on its subtree so children inherit them.
        Directory.CreateDirectory(cgroups.Base);
        if (!cgroups.EnableControllers(cgroups.Base))
            logger.LogWarning("could not enable controllers on base {Base}", cgroups.Base);

        // 3. Delegate the base to the target user: chown the dir + the control files a delegate
        //    needs to write (create children, move PIDs across the slice, set subtree_control).
        if (options.TargetUid is uint uid && options.TargetGid is uint gid)
        {
            ChownDelegated(cgroups.Base, uid, gid);
        }
        else
        {
            logger.LogWarning(
                "no target uid/gid (set KGSM_WATCHDOG_UID/GID or run via sudo); the base stays root-owned");
        }

        // 4. Create the supervisor leaf and place ourselves into it (a leaf can hold processes;
        //    the base cannot, once its subtree_control is non-empty).
        Directory.CreateDirectory(cgroups.SupervisorPath);
        if (options.TargetUid is uint u2 && options.TargetGid is uint g2)
            ChownDelegated(cgroups.SupervisorPath, u2, g2);

        if (!cgroups.AttachToDir(cgroups.SupervisorPath, Environment.ProcessId))
            throw new InvalidOperationException($"failed to enter supervisor cgroup {cgroups.SupervisorPath}");

        // 5. Prepare the runtime socket directory while still root, so the daemon can bind it
        //    after the drop.
        PrepareSocketDir(options.TargetUid, options.TargetGid);

        // 6. Drop privilege (order matters — see class remarks). Skipped if no target user, in
        //    which case the daemon (and the games it spawns) stay root — loudly flagged.
        if (options.TargetUid is uint duid && options.TargetGid is uint dgid)
            DropPrivileges(duid, dgid);
        else
            logger.LogWarning("staying root: games will run as root until KGSM_WATCHDOG_UID/GID is set");

        state.Ready = true;
        state.Detail = $"root-bootstrapped; in {cgroups.SupervisorPath}" +
                       (options.TargetUid is uint fu ? $"; dropped to uid {fu}" : "; STILL ROOT");
        logger.LogInformation("bootstrap complete (root path): {Detail}", state.Detail);
    }

    // ---- delegated path: ensure leaf + enter ----------------------------------------------

    private void RunDelegated()
    {
        if (!cgroups.Supported())
        {
            state.Ready = false;
            state.Detail =
                $"not root and {cgroups.Base} is not a writable delegated base — " +
                "run once as root (or 'kgsm system setup-cgroups'), or launch under systemd with " +
                "User=, Slice=kgsm.slice, Delegate=yes";
            logger.LogError("{Detail}", state.Detail);
            return;
        }

        // Base is delegated; ensure the supervisor leaf (controllers already enabled on the base
        // by setup-cgroups / a prior root boot — best-effort re-enable in case not).
        cgroups.EnableControllers(cgroups.Base);
        Directory.CreateDirectory(cgroups.SupervisorPath);

        // Move into the supervisor leaf. This is an intra-slice move and only succeeds if our
        // CURRENT cgroup is already inside kgsm.slice (systemd placed us via Slice=kgsm.slice).
        // From an outside cgroup (e.g. an SSH login in system.slice) the common ancestor is the
        // root cgroup, which we do not own → EACCES. Report that precisely.
        if (!cgroups.AttachToDir(cgroups.SupervisorPath, Environment.ProcessId))
        {
            state.Ready = false;
            state.Detail =
                $"delegated base is writable but could not enter {cgroups.SupervisorPath} — the " +
                "daemon's own cgroup is outside kgsm.slice. Launch it inside the slice (systemd " +
                "Slice=kgsm.slice, or a root boot that enters the slice then drops privilege).";
            logger.LogError("{Detail}", state.Detail);
            return;
        }

        state.Ready = true;
        state.Detail = $"delegated; in {cgroups.SupervisorPath} (no root step needed)";
        logger.LogInformation("bootstrap complete (delegated path): {Detail}", state.Detail);
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Hand a cgroup directory to a delegate: chown the dir and the three control files cgroup v2
    /// delegation requires a delegate to own (procs to move PIDs, subtree_control to enable
    /// controllers, threads for completeness). cgroup.threads may be absent — best-effort.
    /// </summary>
    private void ChownDelegated(string cgDir, uint uid, uint gid)
    {
        Chown(cgDir, uid, gid);
        Chown(Path.Combine(cgDir, "cgroup.procs"), uid, gid);
        Chown(Path.Combine(cgDir, "cgroup.subtree_control"), uid, gid);
        Chown(Path.Combine(cgDir, "cgroup.threads"), uid, gid, optional: true);
    }

    private void Chown(string path, uint uid, uint gid, bool optional = false)
    {
        if (optional && !File.Exists(path) && !Directory.Exists(path))
            return;
        if (NativeMethods.chown(path, uid, gid) != 0)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            if (!optional)
                logger.LogWarning("chown {Path} -> {Uid}:{Gid} failed (errno {Err})", path, uid, gid, err);
        }
    }

    private void PrepareSocketDir(uint? uid, uint? gid)
    {
        string? dir = Path.GetDirectoryName(options.SocketPath);
        if (string.IsNullOrEmpty(dir))
            return;
        // The root-boot unit does NOT use systemd RuntimeDirectory= (it would re-chown the dir to root
        // on `systemctl reload` and break the dropped re-exec's socket bind — see that unit's header).
        // So the root bootstrap is the SOLE owner of the socket dir: create it, lock it to 0750, and
        // hand it to the target user. It then stays owned by that user across a hot-swap re-exec (which
        // runs as that user and only re-binds — it cannot chown), so the bind succeeds.
        Directory.CreateDirectory(dir);
        try
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not set mode 0750 on socket dir {Dir}", dir);
        }
        if (uid is uint u && gid is uint g)
            Chown(dir, u, g);
    }

    private void DropPrivileges(uint uid, uint gid)
    {
        // setgroups FIRST: discard root's supplementary groups while we still may. We set the
        // single primary gid here; full getgrouplist-based supplementary resolution is a later
        // refinement (PLAN §6 deploy units pass the right context via systemd anyway).
        if (NativeMethods.setgroups((nuint)1, [gid]) != 0)
            ThrowErrno("setgroups");
        if (NativeMethods.setresgid(gid, gid, gid) != 0)
            ThrowErrno("setresgid");
        if (NativeMethods.setresuid(uid, uid, uid) != 0)
            ThrowErrno("setresuid");

        logger.LogInformation("dropped privilege to uid {Uid} gid {Gid}", uid, gid);

        ApplyUserEnvironment(uid);
    }

    /// <summary>
    /// After the uid drop, <c>HOME</c> is still the boot process's (root's). KGSM resolves its XDG
    /// data/config dirs from <c>HOME</c>, so shelling <c>kgsm.sh</c> with <c>HOME=/root</c> reads the
    /// wrong instance store (and can't even create it unprivileged) — every <c>GetInstanceInfo</c>
    /// returns null. Set <c>HOME</c>/<c>USER</c>/<c>LOGNAME</c> to the target user's, the same thing
    /// <c>su -</c> and systemd <c>User=</c> do. Honors <c>KGSM_WATCHDOG_HOME</c>, else resolves from
    /// <c>/etc/passwd</c> (local users — the common service-account case; NSS-only users should set
    /// the override).
    /// </summary>
    private void ApplyUserEnvironment(uint uid)
    {
        string? home = Environment.GetEnvironmentVariable("KGSM_WATCHDOG_HOME");
        string? user = null;
        if (string.IsNullOrEmpty(home))
            (home, user) = ResolvePasswd(uid);

        if (!string.IsNullOrEmpty(home))
        {
            Environment.SetEnvironmentVariable("HOME", home);
            if (!string.IsNullOrEmpty(user))
            {
                Environment.SetEnvironmentVariable("USER", user);
                Environment.SetEnvironmentVariable("LOGNAME", user);
            }
            logger.LogInformation("dropped-user environment: HOME={Home} USER={User}", home, user ?? "(unset)");
        }
        else
        {
            logger.LogWarning(
                "could not resolve HOME for uid {Uid}; kgsm-lib may not find instances — set KGSM_WATCHDOG_HOME", uid);
        }
    }

    private static (string? Home, string? User) ResolvePasswd(uint uid)
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                // name:passwd:uid:gid:gecos:home:shell
                var f = line.Split(':');
                if (f.Length >= 7 && uint.TryParse(f[2], out uint u) && u == uid)
                    return (f[5], f[0]);
            }
        }
        catch
        {
            // fall through to "unresolved"
        }
        return (null, null);
    }

    private static void ThrowErrno(string call)
    {
        int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        throw new InvalidOperationException($"{call} failed (errno {err})");
    }
}
