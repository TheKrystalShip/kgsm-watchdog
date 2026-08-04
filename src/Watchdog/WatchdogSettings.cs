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
public sealed class WatchdogSettings
{
    /// <summary>The configuration section this binds from.</summary>
    public const string Section = "Watchdog";

    /// <summary>
    /// Path to the KGSM executable (<c>kgsm.sh</c>). <b>Required</b> — unlike the monitor (which can
    /// run host-only), the watchdog has nothing to do without KGSM: it reads each instance's spawn
    /// config via kgsm-lib before forking it.
    /// </summary>
    public string KgsmPath { get; set; } = string.Empty;

    /// <summary>Control unix domain socket the daemon listens on.</summary>
    public string SocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>
    /// Permission bits applied to the control socket once it exists, as octal digits (e.g.
    /// <c>"660"</c> — owner+group read/write). The socket can start and kill game servers, so its
    /// filesystem perms are the security boundary: there is no in-daemon authn, that is the
    /// surfaces' job. Malformed input keeps the default.
    /// </summary>
    public string SocketMode { get; set; } = "660";

    /// <summary>cgroup v2 mount point.</summary>
    public string CgroupMountPoint { get; set; } = "/sys/fs/cgroup";

    /// <summary>Fallback cgroup base only; the real base is discovered from <c>/proc/self/cgroup</c>
    /// (systemd delegation).</summary>
    public string CgroupBaseName { get; set; } = "kgsm.slice";

    /// <summary>Controllers to enable on the base subtree so per-instance children inherit them.
    /// Space- or comma-separated.</summary>
    public string CgroupControllers { get; set; } = "cpu memory io pids";

    /// <summary>
    /// Leaf cgroup the daemon itself lives in, a sibling of every instance cgroup under the base.
    /// It must be a non-internal leaf because cgroup v2 forbids processes in a cgroup that has
    /// enabled controllers in <c>subtree_control</c>.
    /// </summary>
    public string SupervisorLeaf { get; set; } = "supervisor";

    /// <summary>How often the crash watcher polls each instance's <c>cgroup.events</c> for
    /// liveness. Floor 50. Cheap at this scale, so 1 Hz is plenty; lower it only to make
    /// crash-detection latency tighter.</summary>
    public int? PollIntervalMs { get; set; }

    /// <summary>
    /// What counts as restartable: <c>always</c> (restart on any exit while desired-running) or
    /// <c>on-failure</c> (leave a clean code-0 exit stopped, restart only crashes). <c>always</c> is
    /// the default because game exit codes are unreliable — many exit 0 on a crash. Any spelling
    /// reducing to "onfailure" selects on-failure; anything else keeps the default.
    /// </summary>
    public string RestartPolicy { get; set; } = "always";

    /// <summary>First-restart delay; doubles each consecutive failure. Floor 0.</summary>
    public int? RestartBaseDelayMs { get; set; }

    /// <summary>Ceiling on the exponential restart delay. Floor 0.</summary>
    public int? RestartMaxDelayMs { get; set; }

    /// <summary>Max consecutive restarts before giving up (<c>phase=failed</c>). Floor 0.</summary>
    public int? RestartMaxRetries { get; set; }

    /// <summary>Uptime after which an instance is "healthy" and its failure counter resets,
    /// seconds. Floor 1.</summary>
    public int? RestartStabilitySeconds { get; set; }

    /// <summary>Post-spawn grace window in which crash-detection is suppressed, seconds.
    /// Floor 0.</summary>
    public int? RestartGraceSeconds { get; set; }

    /// <summary>
    /// On-disk file holding the set of instances the operator left desired-running, so the daemon
    /// can restore supervision after a restart or host reboot — the in-house replacement for
    /// <c>systemctl enable</c> + <c>WantedBy=</c>. Empty (the default) derives it lazily as
    /// <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm-watchdog/desired-state.json</c>, resolved AFTER
    /// the privilege drop so it lands in the dropped KGSM user's data tree — writable by
    /// construction, with no extra privileged setup step. Set this only to relocate it.
    /// </summary>
    public string StateFile { get; set; } = string.Empty;

    /// <summary>
    /// KGSM's instances directory — the root the player-presence ingester watches for container
    /// event channels. Empty (the default) derives it lazily as
    /// <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm/instances</c>, resolved after the privilege drop
    /// so <c>HOME</c> is the dropped KGSM user's. Set this only to relocate it.
    /// </summary>
    public string InstancesDir { get; set; } = string.Empty;

    /// <summary>How often the player-presence ingester scans for channels and tails them. Floor 50.
    /// Presence latency is bounded by this; cheap at this scale, so 1 Hz is plenty.</summary>
    public int? PlayerPresencePollMs { get; set; }

    /// <summary>How often the container lifecycle ingester scans for <c>events/lifecycle.ndjson</c>
    /// channels and tails them, driving UPnP open/close off a container's self-reported
    /// start/stop. Floor 50.</summary>
    public int? ContainerLifecyclePollMs { get; set; }

    /// <summary>How often the console-follow handler polls a native instance's log file for newly
    /// appended lines. Floor 50. Tighter than the presence poll because a human is watching the
    /// stream live, so latency matters; still cheap (one stat plus a short read per client).</summary>
    public int? ConsolePollMs { get; set; }
}
