using System.Text;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.Supervision;

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

    // ---- Increment 2: crash detection + restart ----------------------------------------------

    /// <summary>
    /// How often the crash watcher polls each instance's <c>cgroup.events</c> for liveness.
    /// <c>KGSM_WATCHDOG_POLL_INTERVAL_MS</c>. Default <c>1000</c>. Cheap at this scale (a handful of
    /// instances), so 1 Hz is plenty; lower it only to make crash-detection latency tighter.
    /// </summary>
    public int PollIntervalMs { get; init; } = 1000;

    /// <summary>First-restart delay; doubles each consecutive failure. <c>KGSM_WATCHDOG_RESTART_BASE_DELAY_MS</c>. Default <c>1000</c>.</summary>
    public int RestartBaseDelayMs { get; init; } = 1000;

    /// <summary>Ceiling on the exponential restart delay. <c>KGSM_WATCHDOG_RESTART_MAX_DELAY_MS</c>. Default <c>60000</c>.</summary>
    public int RestartMaxDelayMs { get; init; } = 60_000;

    /// <summary>Max consecutive restarts before giving up ("failed"). <c>KGSM_WATCHDOG_RESTART_MAX_RETRIES</c>. Default <c>5</c>.</summary>
    public int RestartMaxRetries { get; init; } = 5;

    /// <summary>Uptime after which an instance is "healthy" and its failure counter resets. <c>KGSM_WATCHDOG_RESTART_STABILITY_SEC</c>. Default <c>300</c>.</summary>
    public int RestartStabilitySeconds { get; init; } = 300;

    /// <summary>Post-spawn grace window in which crash-detection is suppressed. <c>KGSM_WATCHDOG_RESTART_GRACE_SEC</c>. Default <c>10</c>.</summary>
    public int RestartGraceSeconds { get; init; } = 10;

    /// <summary>
    /// What counts as restartable. <c>KGSM_WATCHDOG_RESTART_POLICY</c> = <c>always</c> (default) | <c>on-failure</c>.
    /// <c>always</c>: restart on any exit while desired-running (the only "stay down" is <c>stop</c>);
    /// <c>on-failure</c>: leave a clean (code 0) exit stopped, restart only crashes. See <see cref="RestartPolicyMode"/>.
    /// </summary>
    public RestartPolicyMode RestartPolicy { get; init; } = RestartPolicyMode.Always;

    // ---- Increment 4: boot persistence (auto-start across restarts) ---------------------------

    /// <summary>
    /// On-disk file holding the set of instances the operator left desired-running, so the daemon can
    /// restore supervision after a restart or host reboot — the in-house replacement for systemd's
    /// <c>systemctl enable</c> + <c>WantedBy=</c>. <c>KGSM_WATCHDOG_STATE_FILE</c>. <b>Empty (the
    /// default)</b> means "derive it lazily" as <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm-watchdog/desired-state.json</c>
    /// — resolved AFTER the privilege drop, so it lands in the dropped KGSM user's data tree (writable
    /// by construction, no extra privileged setup step). Set this only to relocate it.
    /// </summary>
    public string StateFile { get; init; } = string.Empty;

    // ---- Player presence: container event-channel ingester ------------------------------------

    /// <summary>
    /// KGSM's instances directory — the root the player-presence ingester watches for container event
    /// channels (<c>&lt;root&gt;/&lt;blueprint&gt;/&lt;instance&gt;/events/events.ndjson</c>, each instance
    /// dir a symlink to its working dir). <c>KGSM_WATCHDOG_INSTANCES_DIR</c>. <b>Empty (the default)</b>
    /// means "derive it lazily" as <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm/instances</c> — resolved
    /// AFTER the privilege drop, so <c>HOME</c> is the dropped KGSM user's (mirrors
    /// <see cref="StateFile"/>; the watchdog does not inherit KGSM's <c>KGSM_INSTANCES_DIR</c> env). Set
    /// this only to relocate it.
    /// </summary>
    public string InstancesDir { get; init; } = string.Empty;

    /// <summary>
    /// How often the player-presence ingester scans for channels and tails them.
    /// <c>KGSM_WATCHDOG_PLAYER_PRESENCE_POLL_MS</c>. Default <c>1000</c>. Presence latency is bounded by
    /// this; cheap at this scale (a handful of files), so 1 Hz is plenty.
    /// </summary>
    public int PlayerPresencePollMs { get; init; } = 1000;

    // ---- Console stream: live follow of a native instance's stdout ----------------------------

    /// <summary>
    /// How often the <c>GET /console/{name}/follow</c> handler polls a native instance's log file for
    /// newly-appended lines. <c>KGSM_WATCHDOG_CONSOLE_POLL_MS</c>. Default <c>250</c> — tighter than the
    /// presence poll because a human is watching the stream live, so latency matters; still cheap (one
    /// stat + a short read per connected client). Floored at 50ms.
    /// </summary>
    public int ConsolePollMs { get; init; } = 250;

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
            PollIntervalMs = ParseInt(Env("KGSM_WATCHDOG_POLL_INTERVAL_MS"), defaults.PollIntervalMs, min: 50),
            RestartBaseDelayMs = ParseInt(Env("KGSM_WATCHDOG_RESTART_BASE_DELAY_MS"), defaults.RestartBaseDelayMs, min: 0),
            RestartMaxDelayMs = ParseInt(Env("KGSM_WATCHDOG_RESTART_MAX_DELAY_MS"), defaults.RestartMaxDelayMs, min: 0),
            RestartMaxRetries = ParseInt(Env("KGSM_WATCHDOG_RESTART_MAX_RETRIES"), defaults.RestartMaxRetries, min: 0),
            RestartStabilitySeconds = ParseInt(Env("KGSM_WATCHDOG_RESTART_STABILITY_SEC"), defaults.RestartStabilitySeconds, min: 1),
            RestartGraceSeconds = ParseInt(Env("KGSM_WATCHDOG_RESTART_GRACE_SEC"), defaults.RestartGraceSeconds, min: 0),
            RestartPolicy = ParseRestartPolicy(Env("KGSM_WATCHDOG_RESTART_POLICY"), defaults.RestartPolicy),
            StateFile = Env("KGSM_WATCHDOG_STATE_FILE") is { Length: > 0 } st ? st : defaults.StateFile,
            InstancesDir = Env("KGSM_WATCHDOG_INSTANCES_DIR") is { Length: > 0 } id ? id : defaults.InstancesDir,
            PlayerPresencePollMs = ParseInt(Env("KGSM_WATCHDOG_PLAYER_PRESENCE_POLL_MS"), defaults.PlayerPresencePollMs, min: 50),
            ConsolePollMs = ParseInt(Env("KGSM_WATCHDOG_CONSOLE_POLL_MS"), defaults.ConsolePollMs, min: 50),
        };
    }

    /// <summary>Lenient parse: any spelling reducing to "onfailure" selects on-failure; everything else (incl. blank) is always.</summary>
    private static RestartPolicyMode ParseRestartPolicy(string? value, RestartPolicyMode fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        string norm = new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return norm switch
        {
            "onfailure" => RestartPolicyMode.OnFailure,
            "always" => RestartPolicyMode.Always,
            _ => fallback,
        };
    }

    /// <summary>
    /// Every environment variable the daemon reads. The single source of truth for config discovery:
    /// <see cref="DescribeEnvironment"/> renders it for <c>--help</c>, and <see cref="UnknownConfigVars"/>
    /// uses it to flag typo'd <c>KGSM_WATCHDOG_*</c> vars at startup. <b>Add a new knob here</b> when you
    /// add one to <see cref="FromEnvironment"/> (a unit test asserts every name appears in the help).
    /// </summary>
    public static readonly string[] KnownEnvVars =
    [
        "KGSM_WATCHDOG_KGSM_PATH",
        "KGSM_WATCHDOG_KGSM_SOCKET",
        "KGSM_WATCHDOG_SOCKET",
        "KGSM_WATCHDOG_SOCKET_MODE",
        "KGSM_WATCHDOG_CGROUP_MOUNT",
        "KGSM_WATCHDOG_CGROUP_BASE",
        "KGSM_WATCHDOG_CGROUP_CONTROLLERS",
        "KGSM_WATCHDOG_SUPERVISOR_LEAF",
        "KGSM_WATCHDOG_UID",
        "KGSM_WATCHDOG_GID",
        "KGSM_WATCHDOG_HOME",
        "KGSM_WATCHDOG_POLL_INTERVAL_MS",
        "KGSM_WATCHDOG_RESTART_BASE_DELAY_MS",
        "KGSM_WATCHDOG_RESTART_MAX_DELAY_MS",
        "KGSM_WATCHDOG_RESTART_MAX_RETRIES",
        "KGSM_WATCHDOG_RESTART_STABILITY_SEC",
        "KGSM_WATCHDOG_RESTART_GRACE_SEC",
        "KGSM_WATCHDOG_RESTART_POLICY",
        "KGSM_WATCHDOG_STATE_FILE",
        "KGSM_WATCHDOG_INSTANCES_DIR",
        "KGSM_WATCHDOG_PLAYER_PRESENCE_POLL_MS",
        "KGSM_WATCHDOG_CONSOLE_POLL_MS",
    ];

    /// <summary>
    /// Any <c>KGSM_WATCHDOG_*</c> environment variables that are set but not recognised — almost always
    /// a typo silently falling back to a default. Logged as a warning at startup so the config surface
    /// is not invisible (the cost of stringly-typed env config; see PLAN §8 discussion).
    /// <para>
    /// The self-re-exec hot-swap handoff (<see cref="HotSwapHandoff.EnvVarName"/>) shares the
    /// <c>KGSM_WATCHDOG_</c> prefix but is an internal IPC channel set by the predecessor image just before
    /// the <c>execv</c>, NOT an operator config knob — so it is excluded here rather than listed in
    /// <see cref="KnownEnvVars"/> (which would force it into the <c>--help</c> reference).
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> UnknownConfigVars()
    {
        var known = new HashSet<string>(KnownEnvVars, StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string key
                && key.StartsWith("KGSM_WATCHDOG_", StringComparison.Ordinal)
                && key != HotSwapHandoff.EnvVarName   // internal hot-swap handoff channel, not config
                && !known.Contains(key))
                unknown.Add(key);
        }
        unknown.Sort(StringComparer.Ordinal);
        return unknown;
    }

    /// <summary>
    /// The operator-facing configuration reference, rendered for <c>--help</c>. Defaults are read live
    /// from a fresh <see cref="WatchdogOptions"/> so the help can never drift from the real defaults.
    /// </summary>
    public static string DescribeEnvironment()
    {
        var d = new WatchdogOptions();
        var sb = new StringBuilder();

        sb.AppendLine("kgsm-watchdog — resident KGSM native-instance supervisor daemon");
        sb.AppendLine();
        sb.AppendLine("USAGE");
        sb.AppendLine("  kgsm-watchdog [--help|-h]");
        sb.AppendLine();
        sb.AppendLine("CONFIGURATION");
        sb.AppendLine("  All configuration is via environment variables — idiomatic for a systemd/init");
        sb.AppendLine("  daemon (set them in the unit's Environment= / EnvironmentFile=). Exactly one var");
        sb.AppendLine("  is REQUIRED; every other knob has a working default, shown in [brackets].");

        void Section(string title) { sb.AppendLine(); sb.AppendLine(title); }
        void Row(string name, string def, string desc)
            => sb.AppendLine($"  {name,-37} {def,-26} {desc}");

        Section("KGSM integration");
        Row("KGSM_WATCHDOG_KGSM_PATH", "[REQUIRED]", "absolute path to kgsm.sh (read via kgsm-lib for spawn config)");
        Row("KGSM_WATCHDOG_KGSM_SOCKET", $"[{d.KgsmSocketPath}]", "kgsm-lib event socket");

        Section("Control socket (the security boundary is its filesystem perms)");
        Row("KGSM_WATCHDOG_SOCKET", $"[{d.SocketPath}]", "control unix-domain socket path");
        Row("KGSM_WATCHDOG_SOCKET_MODE", "[0660]", "octal perms applied to the socket");

        Section("Cgroup layout (rarely changed)");
        Row("KGSM_WATCHDOG_CGROUP_MOUNT", $"[{d.CgroupMountPoint}]", "cgroup v2 mount point");
        Row("KGSM_WATCHDOG_CGROUP_BASE", $"[{d.CgroupBaseName}]", "KGSM's delegated base cgroup");
        Row("KGSM_WATCHDOG_CGROUP_CONTROLLERS", $"[{string.Join(' ', d.CgroupControllers)}]", "controllers enabled on the base subtree");
        Row("KGSM_WATCHDOG_SUPERVISOR_LEAF", $"[{d.SupervisorLeaf}]", "leaf cgroup the daemon itself lives in");

        Section("Privilege drop (root-boot path)");
        Row("KGSM_WATCHDOG_UID", "[SUDO_UID]", "uid to drop to / chown the subtree to");
        Row("KGSM_WATCHDOG_GID", "[SUDO_GID]", "gid counterpart");
        Row("KGSM_WATCHDOG_HOME", "[from /etc/passwd]", "HOME for the dropped user (kgsm-lib reads XDG dirs from it)");

        Section("Supervision: crash detection + restart");
        Row("KGSM_WATCHDOG_POLL_INTERVAL_MS", $"[{d.PollIntervalMs}]", "how often cgroup.events is polled for liveness");
        Row("KGSM_WATCHDOG_RESTART_POLICY", $"[{d.RestartPolicy.ToString().ToLowerInvariant()}]", "always = restart any exit; on-failure = keep clean code-0 exits stopped");
        Row("KGSM_WATCHDOG_RESTART_BASE_DELAY_MS", $"[{d.RestartBaseDelayMs}]", "first-restart delay; doubles each consecutive failure");
        Row("KGSM_WATCHDOG_RESTART_MAX_DELAY_MS", $"[{d.RestartMaxDelayMs}]", "ceiling on the exponential delay");
        Row("KGSM_WATCHDOG_RESTART_MAX_RETRIES", $"[{d.RestartMaxRetries}]", "consecutive failures before giving up (phase=failed)");
        Row("KGSM_WATCHDOG_RESTART_STABILITY_SEC", $"[{d.RestartStabilitySeconds}]", "uptime after which the failure streak resets");
        Row("KGSM_WATCHDOG_RESTART_GRACE_SEC", $"[{d.RestartGraceSeconds}]", "post-spawn window where crash-detection is suppressed");

        Section("Boot persistence (auto-start across restarts — replaces systemd enable/WantedBy)");
        Row("KGSM_WATCHDOG_STATE_FILE", "[~/.local/share/kgsm-watchdog/desired-state.json]", "desired-running set restored on boot; default under the KGSM user's data dir");

        Section("Player presence (container event-channel ingester)");
        Row("KGSM_WATCHDOG_INSTANCES_DIR", "[~/.local/share/kgsm/instances]", "kgsm instances dir watched for container events/events.ndjson channels");
        Row("KGSM_WATCHDOG_PLAYER_PRESENCE_POLL_MS", $"[{d.PlayerPresencePollMs}]", "how often presence channels are scanned and tailed");

        Section("Console stream (live follow of a native instance's stdout)");
        Row("KGSM_WATCHDOG_CONSOLE_POLL_MS", $"[{d.ConsolePollMs}]", "how often /console/{instance}/follow polls the log for new lines");

        sb.AppendLine();
        sb.AppendLine("CONTROL PLANE (HTTP/1.1 over the unix socket)");
        sb.AppendLine("  GET  /health                    supervisor readiness (200 ready / 503 not + reason)");
        sb.AppendLine("  GET  /ready                     deprecated alias of /health (removed next release)");
        sb.AppendLine("  POST /start/{instance}          spawn into its cgroup, desired-state = running");
        sb.AppendLine("  POST /stop/{instance}           graceful stop -> drain -> cgroup.kill, desired-state = stopped");
        sb.AppendLine("  GET  /status/{instance}         desired/phase/populated/pid/restarts");
        sb.AppendLine("  GET  /list                      all supervised instances");
        sb.AppendLine("  GET  /console/{instance}?tail=N last <=N lines of stdout (native only; finite text/plain)");
        sb.AppendLine("  GET  /console/{instance}/follow live stdout follow from connect (native only; chunked text/plain)");

        return sb.ToString();
    }

    private static uint? ParseId(string? value)
        => uint.TryParse(value, out uint id) ? id : null;

    private static int ParseInt(string? value, int fallback, int min)
        => int.TryParse(value, out int v) && v >= min ? v : fallback;

    private static string[]? ParseControllers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var parts = raw.Split([' ', ',', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts : null;
    }
}
