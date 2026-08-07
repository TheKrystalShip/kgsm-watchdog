using TheKrystalShip.KGSM.LeafConfig;

namespace TheKrystalShip.KGSM.Watchdog;

/// <summary>
/// The daemon's configurable surface, shaped 1:1 with the <c>"Watchdog"</c> section of
/// <c>kgsm-watchdog.settings.json</c>. That file is the source of truth: every knob is declared
/// there with its default, and an environment variable may only override a key that exists in it
/// (<c>Watchdog__SocketPath</c>, <c>Watchdog__PollIntervalMs</c>, …). A variable naming a key this
/// class does not declare sets nothing.
/// </summary>
/// <remarks>
/// Bound, not interpreted: the values here are exactly what was configured, including ones the
/// daemon will clamp or reject. <see cref="WatchdogOptions.FromSettings"/> does the normalizing —
/// octal parsing, floors, the lenient restart-policy spelling — so a bad value stays visible as
/// what was actually asked for. Binding is source-generated (the binder generator is on under
/// <c>PublishAot</c>), so this stays reflection-free.
/// <para>
/// The properties here are the daemon's whole config vocabulary: <see cref="WatchdogOptions"/>
/// derives its <c>--help</c> reference and its typo detection from them, so adding a property is
/// the only step needed to make a knob discoverable.
/// </para>
/// <para>
/// Every number is <b>nullable</b>, and null means "not written" — the coded default in
/// <see cref="WatchdogOptions"/> applies. Two binder behaviours make this load-bearing rather than
/// stylistic: a blank value (<c>Watchdog__PollIntervalMs=</c>, a single stray line in an env file)
/// binds to a non-nullable <see cref="int"/> by throwing, which for this daemon means every native
/// game server goes unsupervised until someone notices; and a JSON null binds to <c>0</c>, silently
/// discarding the default a property initializer here would have carried. Nullable turns both into
/// "unset". A value that is present but is not a number still fails loudly, which is the point of
/// typing it at all.
/// </para>
/// </remarks>
[LeafSection(Section)]
public sealed class WatchdogSettings
{
    /// <summary>The configuration section this binds from.</summary>
    public const string Section = "Watchdog";

    /// <summary>
    /// The lowest value each cadence and counter is allowed to take. Declared once and read by both
    /// <see cref="WatchdogOptions.FromSettings"/>, which raises anything lower, and the leaf
    /// descriptor, which is what the Control Panel rejects against before it restarts the daemon — so
    /// the panel can never accept a value the daemon would silently move.
    /// </summary>
    public static class Floors
    {
        /// <summary>Any poller. Below this the daemon spends more time waking than working.</summary>
        public const int PollMs = 50;

        /// <summary>A delay, a retry count or a grace window may legitimately be zero.</summary>
        public const int Zero = 0;

        /// <summary>An instance has to stay up for some non-zero time to count as healthy.</summary>
        public const int StabilitySeconds = 1;
    }

    /// <summary>
    /// Path to the KGSM executable (<c>kgsm.sh</c>). <b>Required</b> — unlike the monitor (which can
    /// run host-only), the watchdog has nothing to do without KGSM: it reads each instance's spawn
    /// config via kgsm-lib before forking it.
    /// </summary>
    /// <panel>Path to the KGSM executable. Required: the watchdog reads each instance's spawn
    /// configuration through it before forking the game, so it has nothing to supervise without
    /// one.</panel>
    [LeafField("kgsmPath", "KGSM executable", Group = "kgsm", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string KgsmPath { get; set; } = string.Empty;

    /// <summary>Control unix domain socket the daemon listens on.</summary>
    /// <panel>Unix socket the watchdog serves its control plane on. Every surface that starts or stops
    /// a native server reaches it here.</panel>
    [LeafField("socketPath", "Control socket", Group = "socket", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, PairedApiKey = "Api__WatchdogSocketPath")]
    public string SocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>
    /// Permission bits applied to the control socket once it exists, as octal digits (e.g.
    /// <c>"660"</c> — owner+group read/write). The socket can start and kill game servers, so its
    /// filesystem perms are the security boundary: there is no in-daemon authn, that is the
    /// surfaces' job. Malformed input keeps the default.
    /// </summary>
    /// <panel>Octal permission bits applied to the control socket. These are the whole security
    /// boundary — the socket can start and kill game servers, and the daemon does no authentication of
    /// its own.</panel>
    [LeafField("socketMode", "Control socket permissions", Group = "socket", Risk = LeafRisk.Wiring)]
    public string SocketMode { get; set; } = "660";

    /// <summary>cgroup v2 mount point.</summary>
    /// <panel>Where the cgroup v2 hierarchy is mounted. Everything the watchdog supervises lives below
    /// this path.</panel>
    [LeafField("cgroupMount", "Cgroup v2 mount point", Group = "cgroup", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string CgroupMountPoint { get; set; } = "/sys/fs/cgroup";

    /// <summary>Fallback cgroup base only; the real base is discovered from <c>/proc/self/cgroup</c>
    /// (systemd delegation).</summary>
    /// <panel>Fallback name for KGSM's delegated cgroup base. The real base is discovered from the
    /// daemon's own cgroup, so this is used only when that discovery finds nothing.</panel>
    [LeafField("cgroupBase", "Cgroup base (fallback)", Group = "cgroup", Risk = LeafRisk.Wiring)]
    public string CgroupBaseName { get; set; } = "kgsm.slice";

    /// <summary>Controllers to enable on the base subtree so per-instance children inherit them.
    /// Space- or comma-separated.</summary>
    /// <panel>Controllers enabled on the base subtree so each per-instance cgroup inherits them.
    /// Dropping one removes the matching per-server metric.</panel>
    [LeafField("cgroupControllers", "Cgroup controllers", Group = "cgroup", Type = LeafType.Csv,
        Risk = LeafRisk.Wiring)]
    public string CgroupControllers { get; set; } = "cpu,memory,io,pids";

    /// <summary>
    /// Leaf cgroup the daemon itself lives in, a sibling of every instance cgroup under the base.
    /// It must be a non-internal leaf because cgroup v2 forbids processes in a cgroup that has
    /// enabled controllers in <c>subtree_control</c>.
    /// </summary>
    /// <panel>Name of the cgroup the daemon itself sits in, a sibling of every instance cgroup. It has
    /// to be a leaf, because cgroup v2 forbids enabling controllers on a cgroup that holds
    /// processes.</panel>
    [LeafField("supervisorLeaf", "Supervisor leaf cgroup", Group = "cgroup", Risk = LeafRisk.Wiring)]
    public string SupervisorLeaf { get; set; } = "supervisor";

    /// <summary>How often the crash watcher polls each instance's <c>cgroup.events</c> for
    /// liveness. Floor 50. Cheap at this scale, so 1 Hz is plenty; lower it only to make
    /// crash-detection latency tighter.</summary>
    /// <panel>How often each supervised instance is checked for liveness. This bounds how quickly a
    /// crash is noticed.</panel>
    [LeafField("pollIntervalMs", "Supervision poll interval", Group = "supervision",
        Min = Floors.PollMs, Unit = "ms")]
    public int? PollIntervalMs { get; set; }

    /// <summary>
    /// What counts as restartable: <c>always</c> (restart on any exit while desired-running) or
    /// <c>on-failure</c> (leave a clean code-0 exit stopped, restart only crashes). <c>always</c> is
    /// the default because game exit codes are unreliable — many exit 0 on a crash. Any spelling
    /// reducing to "onfailure" selects on-failure; anything else keeps the default.
    /// </summary>
    /// <panel>What counts as restartable. 'always' restarts any exit while the instance is meant to be
    /// running; 'on-failure' leaves a clean exit stopped. Many games exit 0 on a crash, which is why
    /// 'always' is the default.</panel>
    [LeafField("restartPolicy", "Restart policy", Group = "supervision", Type = LeafType.Enum,
        Values = ["always", "on-failure"])]
    public string RestartPolicy { get; set; } = "always";

    /// <summary>First-restart delay; doubles each consecutive failure. Floor 0.</summary>
    /// <panel>How long to wait before the first restart attempt. The delay doubles with each
    /// consecutive failure.</panel>
    [LeafField("restartBaseDelayMs", "First restart delay", Group = "supervision",
        Min = Floors.Zero, Unit = "ms")]
    public int? RestartBaseDelayMs { get; set; }

    /// <summary>Ceiling on the exponential restart delay. Floor 0.</summary>
    /// <panel>Ceiling on the doubling restart delay, so a long failure streak still retries at a
    /// predictable rate.</panel>
    [LeafField("restartMaxDelayMs", "Maximum restart delay", Group = "supervision",
        Min = Floors.Zero, Unit = "ms")]
    public int? RestartMaxDelayMs { get; set; }

    /// <summary>Max consecutive restarts before giving up (<c>phase=failed</c>). Floor 0.</summary>
    /// <panel>How many consecutive failures to tolerate before giving up on an instance and marking it
    /// failed. Zero means never restart.</panel>
    [LeafField("restartMaxRetries", "Maximum consecutive restarts", Group = "supervision", Min = Floors.Zero)]
    public int? RestartMaxRetries { get; set; }

    /// <summary>Uptime after which an instance is "healthy" and its failure counter resets,
    /// seconds. Floor 1.</summary>
    /// <panel>How long an instance must stay up before it counts as healthy and its failure streak
    /// resets.</panel>
    [LeafField("restartStabilitySec", "Stability window", Group = "supervision",
        Min = Floors.StabilitySeconds, Unit = "s")]
    public int? RestartStabilitySeconds { get; set; }

    /// <summary>Post-spawn grace window in which crash-detection is suppressed, seconds.
    /// Floor 0.</summary>
    /// <panel>How long after spawning an instance crash detection stays suppressed, so a slow-starting
    /// game is not mistaken for a crash.</panel>
    [LeafField("restartGraceSec", "Post-spawn grace period", Group = "supervision",
        Min = Floors.Zero, Unit = "s")]
    public int? RestartGraceSeconds { get; set; }

    /// <summary>
    /// On-disk file holding the set of instances the operator left desired-running, so the daemon
    /// can restore supervision after a restart or host reboot — the in-house replacement for
    /// <c>systemctl enable</c> + <c>WantedBy=</c>. Empty (the default) derives it lazily as
    /// <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm-watchdog/desired-state.json</c>, resolved AFTER
    /// the privilege drop so it lands in the dropped KGSM user's data tree — writable by
    /// construction, with no extra privileged setup step. Set this only to relocate it.
    /// </summary>
    /// <panel>File holding the set of instances that should come back up after a restart or reboot.
    /// Empty derives it under the KGSM user's data directory. Pointing it elsewhere orphans the existing
    /// set, and those instances stop being started at boot.</panel>
    [LeafField("stateFile", "Desired-state file", Group = "persistence", Type = LeafType.Path,
        Risk = LeafRisk.Destructive, NoDefault = true)]
    public string StateFile { get; set; } = string.Empty;

    /// <summary>
    /// KGSM's instances directory — the root the player-presence ingester watches for container
    /// event channels. Empty (the default) derives it lazily as
    /// <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm/instances</c>, resolved after the privilege drop
    /// so <c>HOME</c> is the dropped KGSM user's. Set this only to relocate it.
    /// </summary>
    /// <panel>KGSM's instances directory, watched for the per-instance event channels that drive player
    /// presence and container lifecycle. Empty derives it under the KGSM user's data directory; set it
    /// only to relocate it.</panel>
    [LeafField("instancesDir", "Instances directory", Group = "kgsm", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, NoDefault = true)]
    public string InstancesDir { get; set; } = string.Empty;

    /// <summary>How often the player-presence ingester scans for channels and tails them. Floor 50.
    /// Presence latency is bounded by this; cheap at this scale, so 1 Hz is plenty.</summary>
    /// <panel>How often the player-presence channels are scanned and tailed. This bounds how quickly a
    /// join or leave is reported.</panel>
    [LeafField("playerPresencePollMs", "Player presence poll interval", Group = "ingesters",
        Min = Floors.PollMs, Unit = "ms")]
    public int? PlayerPresencePollMs { get; set; }

    /// <summary>How often the container lifecycle ingester scans for <c>events/lifecycle.ndjson</c>
    /// channels and tails them, driving UPnP open/close off a container's self-reported
    /// start/stop. Floor 50.</summary>
    /// <panel>How often a container's self-reported lifecycle channel is scanned and tailed. This drives
    /// port forwarding open and closed as a container starts and stops.</panel>
    [LeafField("containerLifecyclePollMs", "Container lifecycle poll interval", Group = "ingesters",
        Min = Floors.PollMs, Unit = "ms")]
    public int? ContainerLifecyclePollMs { get; set; }

    /// <summary>How often the console-follow handler polls a native instance's log file for newly
    /// appended lines. Floor 50. Tighter than the presence poll because a human is watching the
    /// stream live, so latency matters; still cheap (one stat plus a short read per client).</summary>
    /// <panel>How often a followed console polls a native instance's log for new lines. Tighter than the
    /// other pollers because someone is watching the output live.</panel>
    [LeafField("consolePollMs", "Console follow interval", Group = "ingesters",
        Min = Floors.PollMs, Unit = "ms")]
    public int? ConsolePollMs { get; set; }

    /// <panel>Control socket of the kgsm-firewall authority, which the supervisor asks to open an
    /// instance's ports when it starts and to close them when it stops. An unreachable authority is
    /// logged and never blocks a start.</panel>
    [LeafField("firewallSocket", "Firewall socket", Group = "firewall", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string FirewallSocketPath { get; set; } = "/run/kgsm-firewall/firewall.sock";
}
