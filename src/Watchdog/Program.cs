using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Control;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;
using TheKrystalShip.KGSM.Watchdog.Supervision;

// Self-documenting: an operator with only the compiled binary can discover every config knob.
if (args.Any(a => a is "--help" or "-h" or "help"))
{
    Console.Write(WatchdogOptions.DescribeEnvironment());
    return 0;
}

// Inc 7 Phase 0 — the hot-swap safety-gate contract. Both branches run BEFORE the host is built and
// BEFORE CgroupBootstrap: a swap must be able to interrogate the freshly-deployed binary as a cheap
// subprocess WITHOUT it binding the control socket, entering the slice, or touching cgroups.

// --version: one line, the compiled-in informational version, exit 0.
if (args.Any(a => a is "--version"))
{
    Console.WriteLine(VersionInfo.Informational);
    return 0;
}

// --selfcheck: a no-side-effect runnability probe the swap coordinator invokes on the NEW binary
// before committing to an exec. It only confirms the binary loads and its config parses — it MUST NOT
// bind the socket, run CgroupBootstrap, or touch cgroups. Fast and pure.
if (args.Any(a => a is "--selfcheck"))
{
    try
    {
        WatchdogOptions.FromEnvironment(); // throws if KGSM_WATCHDOG_* config is malformed
        Console.WriteLine($"selfcheck ok {VersionInfo.Informational}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"selfcheck FAILED: {ex.Message}");
        return 1;
    }
}

var options = WatchdogOptions.FromEnvironment();

// Unlike the monitor, the watchdog cannot run "headless" without KGSM: it reads each instance's
// spawn config via kgsm-lib before forking it. No path, nothing to do — fail fast and loud.
if (string.IsNullOrEmpty(options.KgsmPath))
{
    Console.Error.WriteLine("FATAL: KGSM_WATCHDOG_KGSM_PATH is required (absolute path to kgsm.sh).");
    Console.Error.WriteLine("Run 'kgsm-watchdog --help' for the full list of configuration variables.");
    return 1;
}

var builder = WebApplication.CreateSlimBuilder(args);

// Ecosystem-standard logging (see ../tks/logging-convention.md): one journald-native SystemdConsole
// sink (the <N> syslog priority prefix lets `journalctl -p` filter by level). AddConfiguration binds the
// "Logging" section from appsettings.json + env overrides (Logging__LogLevel__Default=Debug) — wired
// explicitly so the level knob is deterministic on the slim builder rather than relying on an implicit
// default. The watchdog's own knobs still come from env (WatchdogOptions.FromEnvironment); logging only.
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddSystemdConsole();

builder.Services.AddSingleton(options);

// The single KGSM chokepoint — used ONLY to read instance config (and, in Inc 2, to watch
// lifecycle events). The watchdog never calls Start/Stop on it (that path spawns detached).
builder.Services.AddKgsmServices(options.KgsmPath, options.KgsmSocketPath);

// Cgroup layer + boot.
builder.Services.AddSingleton<CgroupManager>();
builder.Services.AddSingleton<SupervisorState>();
builder.Services.AddSingleton<CgroupBootstrap>();

// Supervision layer.
builder.Services.AddSingleton(BackoffPolicy.FromOptions(options));
builder.Services.AddSingleton<SpawnEngine>();
builder.Services.AddSingleton<DesiredStateStore>();
// Inc 7 Phase 2 — companion supervision-state.json: persists restart counters / give-up latch so they
// survive ANY daemon death (OOM/SIGKILL), not just a planned hot-swap. Injected into InstanceSupervisor.
builder.Services.AddSingleton<SupervisionStateStore>();
// UPnP port forwarding: process-lifetime network state the supervisor owns (opens on bring-up,
// holds across crash-restart, closes on intended stop). Self-gates on enable_port_forwarding.
builder.Services.AddSingleton<UpnpService>();
builder.Services.AddSingleton<InstanceSupervisor>();

// Inc 7 Phase 3+4 — the self-re-exec hot-swap (Option 3): on SIGHUP (systemctl reload) the daemon
// execv's the freshly-deployed binary IN PLACE (same PID), carrying each live game's stdin-FIFO fd open
// across the exec, so the game never sees stdin EOF. The coordinator runs the --selfcheck safety gate +
// drives the supervisor's produce/exec; the listener bridges SIGHUP to it. SIGTERM is unchanged.
builder.Services.AddSingleton<HotSwapCoordinator>();
builder.Services.AddHostedService<HotSwapSignalListener>();

// Boot auto-start (replaces systemd enable/WantedBy): restore the persisted desired-running set once
// at startup — registered BEFORE the crash watcher so the table is fully restored (a plain
// IHostedService's StartAsync is awaited) before the first reconcile tick runs.
builder.Services.AddHostedService<StartupRestorer>();

// The crash watcher: polls each instance's cgroup.events and drives restart-with-backoff. It is the
// clock; the supervisor holds all the state and makes all the decisions (one decision point).
builder.Services.AddHostedService<CrashWatcher>();

// One-shot post-startup heap trim: hands the startup allocation spike back to the OS once idle (the
// GC otherwise holds ~75 MB of committed-but-free heap that a 1 Hz poller never collects). Pure
// optimization, runs off the startup path.
builder.Services.AddHostedService<StartupMemoryTrimmer>();

// Player-presence ingester: the watchdog's CONTAINER role. Tails each container instance's
// events/events.ndjson channel and re-emits player join/left as kgsm wire events (origin=system).
// Pure file-reader + forwarder — never shells docker, never supervises containers; additive to the
// native supervision above. Self-resolves the kgsm instances dir post-bootstrap.
builder.Services.AddHostedService<PlayerPresenceIngester>();

// Player-presence ingester: the watchdog's NATIVE role. Tails each native instance's own game log
// (instance.LogFile, which SpawnEngine already targets) and emits the SAME player join/left wire events
// (origin=system), matching the blueprint's player_*_regex (read off the Instance via kgsm-lib) with the
// pure NativeLogMatcher. Additive + decoupled from supervision; no spawn-path change.
builder.Services.AddHostedService<NativePlayerPresenceIngester>();

// Control plane: a unix domain socket (no TCP port; the socket's filesystem perms are the
// security boundary, same model as kgsm-monitor).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    if (File.Exists(options.SocketPath))
        File.Delete(options.SocketPath);
    kestrel.ListenUnixSocket(options.SocketPath);
});

var app = builder.Build();

// Boot sequence runs BEFORE the socket binds (app.Run): it delegates + enters the cgroup slice
// and, if we booted as root, drops privilege and prepares the socket directory — so the socket is
// bound by the already-unprivileged daemon. A failed bootstrap does not crash the daemon; it sets
// SupervisorState.Ready=false and /start returns the reason (so the control plane stays diagnosable).
app.Services.GetRequiredService<CgroupBootstrap>().Run();

// The socket only exists once the host is listening — chmod here, not before app.Run (ENOENT).
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        if (OperatingSystem.IsLinux() && File.Exists(options.SocketPath))
            File.SetUnixFileMode(options.SocketPath, options.SocketMode);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "could not set mode on control socket {Socket}", options.SocketPath);
    }
});

app.MapWatchdog();
app.MapConsole();

// Surface typo'd config: a misspelled KGSM_WATCHDOG_* var silently falls back to its default
// otherwise (the cost of stringly-typed env config — make it visible, not invisible).
foreach (var v in WatchdogOptions.UnknownConfigVars())
    app.Logger.LogWarning(
        "unrecognised config variable {Var} is set but has no effect (typo?) — run 'kgsm-watchdog --help' for valid knobs", v);

var ready = app.Services.GetRequiredService<SupervisorState>();
app.Logger.LogInformation(
    "kgsm-watchdog listening on unix:{Socket} — supervisor ready={Ready} ({Detail})",
    options.SocketPath, ready.Ready, ready.Detail);

app.Run();
return 0;
