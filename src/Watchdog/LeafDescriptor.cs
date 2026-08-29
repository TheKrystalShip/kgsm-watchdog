using TheKrystalShip.KGSM.LeafConfig;

// What the Control Panel shows about this daemon, declared beside the configuration it describes.
// TheKrystalShip.KGSM.LeafConfig reads this out of the built assembly and writes
// deploy/kgsm-watchdog.leaf.json; deploy.sh installs that into /var/lib/kgsm/leaves/watchdog.json,
// where kgsm-api scans for it. The daemon itself never reads any of this.

[assembly: Leaf(
    id: "watchdog",
    displayName: "Watchdog",
    unit: "kgsm-watchdog.service",
    role: "Supervises native game-server instances in their own cgroups, holds desired state, and does crash-restart and boot autostart.")]

[assembly: LeafGroup("general", "General", 1)]
[assembly: LeafGroup("kgsm", "KGSM integration", 2)]
[assembly: LeafGroup("socket", "Control socket", 3)]
[assembly: LeafGroup("cgroup", "Cgroup layout", 4)]
[assembly: LeafGroup("supervision", "Crash detection & restart", 5)]
[assembly: LeafGroup("persistence", "Boot persistence", 6)]
[assembly: LeafGroup("ingesters", "Event ingesters & console", 7)]
[assembly: LeafGroup("firewall", "Host firewall", 8)]
[assembly: LeafGroup("network", "Router port forwarding", 9)]

// Lowest precedence first — the same order the daemon resolves them in.
[assembly: LeafFloorSource("appsettings", "/opt/kgsm-watchdog/kgsm-watchdog.settings.json")]
[assembly: LeafFloorSource("systemd-unit", "kgsm-watchdog.service")]
[assembly: LeafFloorSource("env-file", "/etc/kgsm-watchdog/kgsm-watchdog.env")]

[assembly: LeafFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: LeafFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = LeafType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]
