using TheKrystalShip.KGSM.ComponentConfig;

// What the Control Panel shows about this daemon, declared beside the configuration it describes.
// TheKrystalShip.KGSM.ComponentConfig reads this out of the built assembly and writes
// deploy/kgsm-watchdog.leaf.json; deploy.sh installs that into /var/lib/kgsm/leaves/watchdog.json,
// where kgsm-api scans for it. The daemon itself never reads any of this.

[assembly: Leaf(
    id: "watchdog",
    displayName: "Watchdog",
    unit: "kgsm-watchdog.service",
    role: "Supervises native game-server instances in their own cgroups, holds desired state, and does crash-restart and boot autostart.")]

[assembly: ConfigGroup("general", "General", 1)]
[assembly: ConfigGroup("kgsm", "KGSM integration", 2)]
[assembly: ConfigGroup("socket", "Control socket", 3)]
[assembly: ConfigGroup("cgroup", "Cgroup layout", 4)]
[assembly: ConfigGroup("supervision", "Crash detection & restart", 5)]
[assembly: ConfigGroup("persistence", "Boot persistence", 6)]
[assembly: ConfigGroup("ingesters", "Event ingesters & console", 7)]
[assembly: ConfigGroup("firewall", "Host firewall", 8)]
[assembly: ConfigGroup("network", "Router port forwarding", 9)]

// Lowest precedence first — the same order the daemon resolves them in.
[assembly: ConfigFloorSource("appsettings", "/opt/kgsm-watchdog/kgsm-watchdog.settings.json")]
[assembly: ConfigFloorSource("systemd-unit", "kgsm-watchdog.service")]
[assembly: ConfigFloorSource("env-file", "/etc/kgsm-watchdog/kgsm-watchdog.env")]

[assembly: ConfigFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: ConfigFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = ConfigType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]
