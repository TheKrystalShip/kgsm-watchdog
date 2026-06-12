namespace TheKrystalShip.KGSM.Watchdog;

/// <summary>
/// Runtime configuration, sourced from environment variables (the idiomatic
/// mechanism for a systemd daemon — see <c>Environment=</c> in the unit file).
/// Reading is deliberately reflection-free (no config-binding source generator)
/// so the daemon stays Native-AOT clean. Most knobs have sane defaults;
/// <see cref="KgsmPath"/> is the one hard requirement.
/// </summary>
public sealed class WatchdogOptions
{
    /// <summary>Control unix domain socket the daemon listens on. <c>KGSM_WATCHDOG_SOCKET</c>.</summary>
    public string SocketPath { get; init; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>
    /// Permission bits applied to the control socket once it exists. <c>KGSM_WATCHDOG_SOCKET_MODE</c>
    /// (octal, e.g. <c>660</c>). Default <c>0660</c> — owner+group read/write. The socket can
    /// start/kill game servers, so its filesystem perms are the security boundary (no in-daemon
    /// authn; that is the surfaces' job — see PLAN §5/§8).
    /// </summary>
    public UnixFileMode SocketMode { get; init; } =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>
    /// Path to the KGSM executable (<c>kgsm.sh</c>). <c>KGSM_WATCHDOG_KGSM_PATH</c>. <b>Required</b> —
    /// unlike the monitor (which can run host-only), the watchdog has nothing to do without KGSM:
    /// it reads each instance's spawn config via kgsm-lib before forking it.
    /// </summary>
    public string KgsmPath { get; init; } = string.Empty;

    /// <summary>
    /// Unix socket kgsm-lib uses for KGSM event delivery. <c>KGSM_WATCHDOG_KGSM_SOCKET</c>.
    /// Wired in Increment 2 (lifecycle-event awareness); passed to <c>AddKgsmServices</c> now.
    /// </summary>
    public string KgsmSocketPath { get; init; } = "/run/kgsm-watchdog/events.sock";

    /// <summary>cgroup v2 mount point. <c>KGSM_WATCHDOG_CGROUP_MOUNT</c>. Default <c>/sys/fs/cgroup</c>.</summary>
    public string CgroupMountPoint { get; init; } = "/sys/fs/cgroup";

    /// <summary>KGSM's delegated cgroup base. <c>KGSM_WATCHDOG_CGROUP_BASE</c>. Default <c>kgsm.slice</c>.</summary>
    public string CgroupBaseName { get; init; } = "kgsm.slice";

    /// <summary>
    /// Controllers to enable on the base subtree so per-instance children inherit them.
    /// <c>KGSM_WATCHDOG_CGROUP_CONTROLLERS</c> (space/comma-separated). Default <c>cpu memory io pids</c>.
    /// </summary>
    public IReadOnlyList<string> CgroupControllers { get; init; } = ["cpu", "memory", "io", "pids"];

    /// <summary>
    /// Leaf cgroup the daemon itself lives in, a sibling of every instance cgroup under the base
    /// (<c>kgsm.slice/&lt;leaf&gt;</c>). It must be a non-internal leaf because cgroup v2 forbids
    /// processes in a cgroup that has enabled controllers in <c>subtree_control</c>.
    /// <c>KGSM_WATCHDOG_SUPERVISOR_LEAF</c>. Default <c>supervisor</c>.
    /// </summary>
    public string SupervisorLeaf { get; init; } = "supervisor";

    /// <summary>
    /// uid to drop to after the root-boot bootstrap, and the owner the delegated subtree is
    /// chowned to. <c>KGSM_WATCHDOG_UID</c>, else <c>SUDO_UID</c> (set by <c>sudo</c>), else null.
    /// Null with euid 0 means we stay root and log a warning (games would run as root — not desired).
    /// </summary>
    public uint? TargetUid { get; init; }

    /// <summary>gid counterpart to <see cref="TargetUid"/>. <c>KGSM_WATCHDOG_GID</c>, else <c>SUDO_GID</c>, else null.</summary>
    public uint? TargetGid { get; init; }

    /// <summary>Absolute path of KGSM's delegated base: <c>{CgroupMountPoint}/{CgroupBaseName}</c>.</summary>
    public string CgroupBasePath => $"{CgroupMountPoint}/{CgroupBaseName}";

    public static WatchdogOptions FromEnvironment()
    {
        static string? Env(string key) => Environment.GetEnvironmentVariable(key);

        var defaults = new WatchdogOptions();

        UnixFileMode mode = defaults.SocketMode;
        if (Env("KGSM_WATCHDOG_SOCKET_MODE") is { Length: > 0 } modeStr)
        {
            try { mode = (UnixFileMode)Convert.ToInt32(modeStr, 8); }
            catch (Exception) { /* malformed octal -> keep default */ }
        }

        return new WatchdogOptions
        {
            SocketPath = Env("KGSM_WATCHDOG_SOCKET") is { Length: > 0 } s ? s : defaults.SocketPath,
            SocketMode = mode,
            KgsmPath = Env("KGSM_WATCHDOG_KGSM_PATH") is { Length: > 0 } kp ? kp : defaults.KgsmPath,
            KgsmSocketPath = Env("KGSM_WATCHDOG_KGSM_SOCKET") is { Length: > 0 } ks ? ks : defaults.KgsmSocketPath,
            CgroupMountPoint = Env("KGSM_WATCHDOG_CGROUP_MOUNT") is { Length: > 0 } cm ? cm : defaults.CgroupMountPoint,
            CgroupBaseName = Env("KGSM_WATCHDOG_CGROUP_BASE") is { Length: > 0 } cb ? cb : defaults.CgroupBaseName,
            CgroupControllers = ParseControllers(Env("KGSM_WATCHDOG_CGROUP_CONTROLLERS")) ?? defaults.CgroupControllers,
            SupervisorLeaf = Env("KGSM_WATCHDOG_SUPERVISOR_LEAF") is { Length: > 0 } sl ? sl : defaults.SupervisorLeaf,
            TargetUid = ParseId(Env("KGSM_WATCHDOG_UID")) ?? ParseId(Env("SUDO_UID")),
            TargetGid = ParseId(Env("KGSM_WATCHDOG_GID")) ?? ParseId(Env("SUDO_GID")),
        };
    }

    private static uint? ParseId(string? value)
        => uint.TryParse(value, out uint id) ? id : null;

    private static string[]? ParseControllers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var parts = raw.Split([' ', ',', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts : null;
    }
}
