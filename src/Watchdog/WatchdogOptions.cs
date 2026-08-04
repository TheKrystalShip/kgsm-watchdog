using System.Text;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog;

/// <summary>
/// The normalized runtime view of <see cref="WatchdogSettings"/> — what the daemon runs on, after
/// octal parsing, the lenient restart-policy spelling and the per-knob floors. Configuration is
/// declared in the <c>"Watchdog"</c> section of <c>kgsm-watchdog.settings.json</c> and overridden
/// per-key by environment variables (<c>Watchdog__PollIntervalMs</c>); this type is the result of
/// that, not a second place to configure anything. Most knobs have sane defaults;
/// <see cref="KgsmPath"/> is the one hard requirement.
/// </summary>
/// <remarks>
/// Kept separate from the bound settings so the raw configuration stays inspectable: a value the
/// daemon clamped is still visible as what was configured. Binding is source-generated, so the
/// daemon stays Native-AOT clean.
/// </remarks>
public sealed class WatchdogOptions
{
    /// <summary>Control unix domain socket the daemon listens on. <c>Watchdog__SocketPath</c>.</summary>
    public string SocketPath { get; init; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>
    /// Permission bits applied to the control socket once it exists. <c>Watchdog__SocketMode</c>
    /// (octal, e.g. <c>660</c>). Default <c>0660</c> — owner+group read/write. The socket can
    /// start/kill game servers, so its filesystem perms are the security boundary (no in-daemon
    /// authn; that is the surfaces' job — see PLAN §5/§8).
    /// </summary>
    public UnixFileMode SocketMode { get; init; } =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>
    /// Path to the KGSM executable (<c>kgsm.sh</c>). <c>Watchdog__KgsmPath</c>. <b>Required</b> —
    /// unlike the monitor (which can run host-only), the watchdog has nothing to do without KGSM:
    /// it reads each instance's spawn config via kgsm-lib before forking it.
    /// </summary>
    public string KgsmPath { get; init; } = string.Empty;

    /// <summary>cgroup v2 mount point. <c>Watchdog__CgroupMountPoint</c>. Default <c>/sys/fs/cgroup</c>.</summary>
    public string CgroupMountPoint { get; init; } = "/sys/fs/cgroup";

    /// <summary>KGSM's delegated cgroup base. <c>Watchdog__CgroupBaseName</c>. Default <c>kgsm.slice</c>.</summary>
    public string CgroupBaseName { get; init; } = "kgsm.slice";

    /// <summary>
    /// Controllers to enable on the base subtree so per-instance children inherit them.
    /// <c>Watchdog__CgroupControllers</c> (space/comma-separated). Default <c>cpu memory io pids</c>.
    /// </summary>
    public IReadOnlyList<string> CgroupControllers { get; init; } = ["cpu", "memory", "io", "pids"];

    /// <summary>
    /// Leaf cgroup the daemon itself lives in, a sibling of every instance cgroup under the base
    /// (<c>kgsm.slice/&lt;leaf&gt;</c>). It must be a non-internal leaf because cgroup v2 forbids
    /// processes in a cgroup that has enabled controllers in <c>subtree_control</c>.
    /// <c>Watchdog__SupervisorLeaf</c>. Default <c>supervisor</c>.
    /// </summary>
    public string SupervisorLeaf { get; init; } = "supervisor";

    // ---- Increment 2: crash detection + restart ----------------------------------------------

    /// <summary>
    /// How often the crash watcher polls each instance's <c>cgroup.events</c> for liveness.
    /// <c>Watchdog__PollIntervalMs</c>. Default <c>1000</c>. Cheap at this scale (a handful of
    /// instances), so 1 Hz is plenty; lower it only to make crash-detection latency tighter.
    /// </summary>
    public int PollIntervalMs { get; init; } = 1000;

    /// <summary>First-restart delay; doubles each consecutive failure. <c>Watchdog__RestartBaseDelayMs</c>. Default <c>1000</c>.</summary>
    public int RestartBaseDelayMs { get; init; } = 1000;

    /// <summary>Ceiling on the exponential restart delay. <c>Watchdog__RestartMaxDelayMs</c>. Default <c>60000</c>.</summary>
    public int RestartMaxDelayMs { get; init; } = 60_000;

    /// <summary>Max consecutive restarts before giving up ("failed"). <c>Watchdog__RestartMaxRetries</c>. Default <c>5</c>.</summary>
    public int RestartMaxRetries { get; init; } = 5;

    /// <summary>Uptime after which an instance is "healthy" and its failure counter resets. <c>Watchdog__RestartStabilitySeconds</c>. Default <c>300</c>.</summary>
    public int RestartStabilitySeconds { get; init; } = 300;

    /// <summary>Post-spawn grace window in which crash-detection is suppressed. <c>Watchdog__RestartGraceSeconds</c>. Default <c>10</c>.</summary>
    public int RestartGraceSeconds { get; init; } = 10;

    /// <summary>
    /// What counts as restartable. <c>Watchdog__RestartPolicy</c> = <c>always</c> (default) | <c>on-failure</c>.
    /// <c>always</c>: restart on any exit while desired-running (the only "stay down" is <c>stop</c>);
    /// <c>on-failure</c>: leave a clean (code 0) exit stopped, restart only crashes. See <see cref="RestartPolicyMode"/>.
    /// </summary>
    public RestartPolicyMode RestartPolicy { get; init; } = RestartPolicyMode.Always;

    // ---- Increment 4: boot persistence (auto-start across restarts) ---------------------------

    /// <summary>
    /// On-disk file holding the set of instances the operator left desired-running, so the daemon can
    /// restore supervision after a restart or host reboot — the in-house replacement for systemd's
    /// <c>systemctl enable</c> + <c>WantedBy=</c>. <c>Watchdog__StateFile</c>. <b>Empty (the
    /// default)</b> means "derive it lazily" as <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm-watchdog/desired-state.json</c>
    /// — resolved AFTER the privilege drop, so it lands in the dropped KGSM user's data tree (writable
    /// by construction, no extra privileged setup step). Set this only to relocate it.
    /// </summary>
    public string StateFile { get; init; } = string.Empty;

    // ---- Player presence: container event-channel ingester ------------------------------------

    /// <summary>
    /// KGSM's instances directory — the root the player-presence ingester watches for container event
    /// channels (<c>&lt;root&gt;/&lt;blueprint&gt;/&lt;instance&gt;/events/events.ndjson</c>, each instance
    /// dir a symlink to its working dir). <c>Watchdog__InstancesDir</c>. <b>Empty (the default)</b>
    /// means "derive it lazily" as <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm/instances</c> — resolved
    /// AFTER the privilege drop, so <c>HOME</c> is the dropped KGSM user's (mirrors
    /// <see cref="StateFile"/>; the watchdog does not inherit KGSM's <c>KGSM_INSTANCES_DIR</c> env). Set
    /// this only to relocate it.
    /// </summary>
    public string InstancesDir { get; init; } = string.Empty;

    /// <summary>
    /// How often the player-presence ingester scans for channels and tails them.
    /// <c>Watchdog__PlayerPresencePollMs</c>. Default <c>1000</c>. Presence latency is bounded by
    /// this; cheap at this scale (a handful of files), so 1 Hz is plenty.
    /// </summary>
    public int PlayerPresencePollMs { get; init; } = 1000;

    // ---- Container lifecycle: UPnP + host-visible run-state ingester --------------------------

    /// <summary>
    /// How often the container lifecycle ingester scans for
    /// <c>events/lifecycle.ndjson</c> channels and tails them, driving <see cref="TheKrystalShip.KGSM.Watchdog.PortForwarding.UpnpService"/>
    /// open/close off a container's self-reported <c>instance_started</c>/<c>instance_stopping</c>.
    /// <c>Watchdog__ContainerLifecyclePollMs</c>. Default <c>1000</c> (same default as
    /// <see cref="PlayerPresencePollMs"/> — same instances-dir scan cost, no reason to diverge).
    /// </summary>
    public int ContainerLifecyclePollMs { get; init; } = 1000;

    // ---- Console stream: live follow of a native instance's stdout ----------------------------

    /// <summary>
    /// How often the <c>GET /console/{name}/follow</c> handler polls a native instance's log file for
    /// newly-appended lines. <c>Watchdog__ConsolePollMs</c>. Default <c>250</c> — tighter than the
    /// presence poll because a human is watching the stream live, so latency matters; still cheap (one
    /// stat + a short read per connected client). Floored at 50ms.
    /// </summary>
    public int ConsolePollMs { get; init; } = 250;

    /// <summary>Absolute path of KGSM's delegated base: <c>{CgroupMountPoint}/{CgroupBaseName}</c>.</summary>
    public string CgroupBasePath => $"{CgroupMountPoint}/{CgroupBaseName}";

    /// <summary>
    /// Normalizes bound configuration into the runtime view: octal socket mode, the lenient
    /// restart-policy spelling, and the per-knob floors. A value below its floor is raised to the
    /// floor — the nearest legal value to what was asked for — rather than reverting to the coded
    /// default, which would run at a figure nobody named.
    /// </summary>
    public static WatchdogOptions FromSettings(WatchdogSettings s)
    {
        var defaults = new WatchdogOptions();

        return new WatchdogOptions
        {
            SocketPath = Or(s.SocketPath, defaults.SocketPath),
            SocketMode = ParseMode(s.SocketMode, defaults.SocketMode),
            KgsmPath = s.KgsmPath?.Trim() ?? string.Empty,
            CgroupMountPoint = Or(s.CgroupMountPoint, defaults.CgroupMountPoint),
            CgroupBaseName = Or(s.CgroupBaseName, defaults.CgroupBaseName),
            CgroupControllers = ParseControllers(s.CgroupControllers) ?? defaults.CgroupControllers,
            SupervisorLeaf = Or(s.SupervisorLeaf, defaults.SupervisorLeaf),
            PollIntervalMs = Floor(s.PollIntervalMs ?? defaults.PollIntervalMs, 50),
            RestartBaseDelayMs = Floor(s.RestartBaseDelayMs ?? defaults.RestartBaseDelayMs, 0),
            RestartMaxDelayMs = Floor(s.RestartMaxDelayMs ?? defaults.RestartMaxDelayMs, 0),
            RestartMaxRetries = Floor(s.RestartMaxRetries ?? defaults.RestartMaxRetries, 0),
            RestartStabilitySeconds = Floor(s.RestartStabilitySeconds ?? defaults.RestartStabilitySeconds, 1),
            RestartGraceSeconds = Floor(s.RestartGraceSeconds ?? defaults.RestartGraceSeconds, 0),
            RestartPolicy = ParseRestartPolicy(s.RestartPolicy, defaults.RestartPolicy),
            StateFile = s.StateFile?.Trim() ?? string.Empty,
            InstancesDir = s.InstancesDir?.Trim() ?? string.Empty,
            PlayerPresencePollMs = Floor(s.PlayerPresencePollMs ?? defaults.PlayerPresencePollMs, 50),
            ContainerLifecyclePollMs = Floor(s.ContainerLifecyclePollMs ?? defaults.ContainerLifecyclePollMs, 50),
            ConsolePollMs = Floor(s.ConsolePollMs ?? defaults.ConsolePollMs, 50),
        };
    }

    private static int Floor(int value, int min) => value < min ? min : value;

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static UnixFileMode ParseMode(string? octal, UnixFileMode fallback)
    {
        if (string.IsNullOrWhiteSpace(octal))
            return fallback;
        try { return (UnixFileMode)Convert.ToInt32(octal.Trim(), 8); }
        catch (Exception) { return fallback; }   // malformed octal -> keep the default
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
    /// The environment-variable spelling of every knob, derived from <see cref="WatchdogSettings"/>
    /// rather than listed by hand. Adding a property to that class is the only step needed to make a
    /// knob discoverable: <see cref="DescribeEnvironment"/> renders these for <c>--help</c> and
    /// <see cref="UnknownConfigVars"/> flags anything else in the namespace as a typo.
    /// </summary>
    /// <remarks>
    /// The property names are read from the settings type, so a hand-maintained list can no longer
    /// fall behind the knobs it claims to describe. This runs once at startup and once per
    /// <c>--help</c>, both outside any hot path.
    /// </remarks>
    public static readonly string[] KnownEnvVars =
        [.. typeof(WatchdogSettings)
            .GetProperties()
            .Select(p => $"{WatchdogSettings.Section}__{p.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// Any <c>Watchdog__*</c> environment variables that are set but not recognised — almost always a
    /// typo binding to nothing and leaving the default in place. Logged as a warning at startup so a
    /// misspelled knob is visible rather than silently inert.
    /// </summary>
    /// <remarks>
    /// The hot-swap handoff channel (<see cref="HotSwapHandoff.EnvVarName"/>) lives in a separate
    /// namespace from the <c>Watchdog__</c> config one, so an internal IPC variable cannot be
    /// mistaken for a config knob and needs no exclusion here.
    /// </remarks>
    public static IReadOnlyList<string> UnknownConfigVars()
    {
        string prefix = $"{WatchdogSettings.Section}__";
        var known = new HashSet<string>(KnownEnvVars, StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string key
                && key.StartsWith(prefix, StringComparison.Ordinal)
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
        sb.AppendLine("  kgsm-watchdog.settings.json (beside the binary) declares every knob with its");
        sb.AppendLine("  default. An environment variable overrides one key of it — set them in the unit's");
        sb.AppendLine("  Environment= / EnvironmentFile=. A variable naming a key the file does not declare");
        sb.AppendLine("  binds to nothing. Exactly one knob is REQUIRED; defaults are shown in [brackets].");

        void Section(string title) { sb.AppendLine(); sb.AppendLine(title); }
        void Row(string name, string def, string desc)
            => sb.AppendLine($"  {name,-38} {def,-26} {desc}");

        Section("KGSM integration");
        Row("Watchdog__KgsmPath", "[REQUIRED]", "absolute path to kgsm.sh (read via kgsm-lib for spawn config)");

        Section("Control socket (the security boundary is its filesystem perms)");
        Row("Watchdog__SocketPath", $"[{d.SocketPath}]", "control unix-domain socket path");
        Row("Watchdog__SocketMode", "[0660]", "octal perms applied to the socket");

        Section("Cgroup layout (rarely changed)");
        Row("Watchdog__CgroupMountPoint", $"[{d.CgroupMountPoint}]", "cgroup v2 mount point");
        Row("Watchdog__CgroupBaseName", $"[{d.CgroupBaseName}]", "fallback base only; the real base is discovered from /proc/self/cgroup (systemd delegation)");
        Row("Watchdog__CgroupControllers", $"[{string.Join(' ', d.CgroupControllers)}]", "controllers enabled on the base subtree");
        Row("Watchdog__SupervisorLeaf", $"[{d.SupervisorLeaf}]", "leaf cgroup the daemon itself lives in (under the delegated base)");

        Section("Supervision: crash detection + restart");
        Row("Watchdog__PollIntervalMs", $"[{d.PollIntervalMs}]", "how often cgroup.events is polled for liveness");
        Row("Watchdog__RestartPolicy", $"[{d.RestartPolicy.ToString().ToLowerInvariant()}]", "always = restart any exit; on-failure = keep clean code-0 exits stopped");
        Row("Watchdog__RestartBaseDelayMs", $"[{d.RestartBaseDelayMs}]", "first-restart delay; doubles each consecutive failure");
        Row("Watchdog__RestartMaxDelayMs", $"[{d.RestartMaxDelayMs}]", "ceiling on the exponential delay");
        Row("Watchdog__RestartMaxRetries", $"[{d.RestartMaxRetries}]", "consecutive failures before giving up (phase=failed)");
        Row("Watchdog__RestartStabilitySeconds", $"[{d.RestartStabilitySeconds}]", "uptime after which the failure streak resets");
        Row("Watchdog__RestartGraceSeconds", $"[{d.RestartGraceSeconds}]", "post-spawn window where crash-detection is suppressed");

        Section("Boot persistence (auto-start across restarts — replaces systemd enable/WantedBy)");
        Row("Watchdog__StateFile", "[~/.local/share/kgsm-watchdog/desired-state.json]", "desired-running set restored on boot; default under the KGSM user's data dir");

        Section("Player presence (container event-channel ingester)");
        Row("Watchdog__InstancesDir", "[~/.local/share/kgsm/instances]", "kgsm instances dir watched for container events/events.ndjson channels");
        Row("Watchdog__PlayerPresencePollMs", $"[{d.PlayerPresencePollMs}]", "how often presence channels are scanned and tailed");

        Section("Container lifecycle (UPnP + host-visible run-state ingester)");
        Row("Watchdog__ContainerLifecyclePollMs", $"[{d.ContainerLifecyclePollMs}]", "how often events/lifecycle.ndjson channels are scanned and tailed");

        Section("Console stream (live follow of a native instance's stdout)");
        Row("Watchdog__ConsolePollMs", $"[{d.ConsolePollMs}]", "how often /console/{instance}/follow polls the log for new lines");

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
