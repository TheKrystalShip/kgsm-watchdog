using Microsoft.Extensions.Configuration;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Core.Interfaces;

using TheKrystalShip.KGSM.Services;
using TheKrystalShip.KGSM.Watchdog.Events;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Control;
using TheKrystalShip.KGSM.Watchdog.Firewall;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;
using TheKrystalShip.KGSM.Watchdog.Supervision;
using TheKrystalShip.KGSM.Lifecycle;

// When THIS IMAGE began, captured before anything else runs.
//
// ⚠ Not the process start, which is what LeafLifecycle reads by default and what every other leaf
// wants. A hot-swap `execve`s a new image into the SAME process id, so the OS keeps reporting the
// original start — and a swap of a daemon that had been up four hours reported a four-hour startup
// time. The process really did start then; it is simply the wrong clock for an image that replaced
// itself. This one is right for both paths: on a cold start it differs from the process start only by
// runtime init, and on a swap it is the only honest answer available.
DateTimeOffset imageStartedAt = DateTimeOffset.UtcNow;

// The daemon's configuration sources, in precedence order, applied identically wherever config is
// read. kgsm-watchdog.settings.json (beside the binary) declares every knob with its default; an
// environment variable overrides one key of it.
//
// Two things about this are load-bearing:
//   1. The file is resolved from AppContext.BaseDirectory, not the content root. Under a systemd
//      unit with no WorkingDirectory the content root is "/", so default discovery finds nothing
//      and every setting in the file silently fails to apply.
//   2. Environment variables come LAST so they win. Configuration resolves by source order, and a
//      file added after the environment provider outranks it — an override would then read as
//      applied while changing nothing.
// The file is named kgsm-watchdog.settings.json rather than appsettings.json (the ecosystem
// convention, kgsm-<leaf>.settings.json) so it cannot collide with a sibling service's config.
// optional:true so a missing file never stops the daemon.
static void AddWatchdogConfiguration(IConfigurationBuilder cfg) => cfg
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "kgsm-watchdog.settings.json"),
        optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

// Read config the same way the host will, but without building one — --selfcheck and the required
// -knob check below both run before the host exists and must not bind or touch anything.
static WatchdogOptions ReadOptions()
{
    var cfg = new ConfigurationBuilder();
    AddWatchdogConfiguration(cfg);
    return WatchdogOptions.FromSettings(
        cfg.Build().GetSection(WatchdogSettings.Section).Get<WatchdogSettings>() ?? new WatchdogSettings());
}

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
        ReadOptions(); // throws if the settings file or a Watchdog__* override is malformed
        Console.WriteLine($"selfcheck ok {VersionInfo.Informational}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"selfcheck FAILED: {ex.Message}");
        return 1;
    }
}

var options = ReadOptions();

// Unlike the monitor, the watchdog cannot run "headless" without KGSM: it reads each instance's
// spawn config via kgsm-lib before forking it. No path, nothing to do — fail fast and loud.
if (string.IsNullOrEmpty(options.KgsmPath))
{
    Console.Error.WriteLine("FATAL: Watchdog__KgsmPath is required (absolute path to kgsm.sh).");
    Console.Error.WriteLine("Run 'kgsm-watchdog --help' for the full list of configuration variables.");
    return 1;
}

// ContentRootPath is pinned to the binary's own directory rather than left to default to the
// process working directory. The unit starts the daemon with no WorkingDirectory, so that default
// is "/", and the builder installs its own appsettings.json providers with reloadOnChange:true —
// which hangs a RECURSIVE FileSystemWatcher off the content root. Rooted at "/", that watch walks
// the entire filesystem and takes an inotify watch per directory (~165k here), exhausting the
// per-user fs.inotify.max_user_watches budget the daemon's own supervised game servers draw from.
// A game that cannot get a watch fails to boot, and a game that fails to boot is a game this
// daemon restarts forever. The path is AppContext.BaseDirectory for the same reason the settings
// file is (see AddWatchdogConfiguration): it is the one directory that is correct no matter where
// the process was started from.
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// The same sources the options above were read from, so the host and the pre-host config cannot
// diverge. See AddWatchdogConfiguration for why the path and the ordering are what they are.
AddWatchdogConfiguration(builder.Configuration);

// Ecosystem-standard logging (see ../tks/logging-convention.md): one journald-native SystemdConsole
// sink (the <N> syslog priority prefix lets `journalctl -p` filter by level). AddConfiguration binds the
// "Logging" section from the settings file plus any Logging__LogLevel__Default override — wired
// explicitly so the level knob is deterministic on the slim builder rather than relying on an implicit
// default. This is also where "Microsoft.AspNetCore": "Warning" (in the settings file) takes effect, which
// silences ASP.NET's ~5-Information-lines-per-request chatter — at Information the surfaces' constant
// /health·/list·/enabled polling both floods journald (rate-limiting away useful lines) and allocates on
// every poll, feeding the heap growth the MemoryTrimmer then has to reclaim.
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddSystemdConsole();

builder.Services.AddSingleton(options);

// The single KGSM chokepoint — used ONLY to read instance config. The watchdog never calls
// Start/Stop on it (that path spawns detached) and consumes no engine events: it registers no
// handler and never resolves IEventService, so the lazily-registered event singletons are never
// constructed and nothing is read.
builder.Services.AddKgsmServices(options.KgsmPath);

// Cgroup layer + boot.
builder.Services.AddSingleton<CgroupManager>();
builder.Services.AddSingleton<SupervisorState>();
builder.Services.AddSingleton<CgroupBootstrap>();

// Supervision layer.
builder.Services.AddSingleton(BackoffPolicy.FromOptions(options));
builder.Services.AddSingleton<SpawnEngine>();
// Where all three state files live, resolved once: Watchdog__StateFile > $STATE_DIRECTORY (systemd's
// StateDirectory=kgsm-watchdog) > the XDG data home. Carries state over from a home-directory layout
// on first use, which the autostart set depends on to survive the move.
builder.Services.AddSingleton<StatePathResolver>();
// This daemon's own event journal. It records what the watchdog itself did — the process it spawned,
// the port it opened, the readiness line it saw — instead of spawning kgsm.sh to write each one down,
// which cost a bash bootstrap, a sourced library and a jq call per event.
//
// The producer id is the only input: it decides the directory a reader scans for, the version stamped
// on every event, and the derived system:watchdog actor. ⚠ Deliberately NOT StatePathResolver's answer,
// which the other three state files use — that resolver can land on a home-directory layout, and a
// journal there is one no reader on this host would ever find. A journal has to sit where it can be
// read; KGSM_JOURNAL_STATE_ROOT moves it for a run that must not touch this host's record.
builder.Services.AddKgsmJournal(WatchdogJournal.ProducerId, typeof(Program).Assembly);
builder.Services.AddSingleton<WatchdogJournal>();
// What this daemon says about ITSELF, as opposed to about the instances it supervises. Separate from
// WatchdogJournal because the two answer to different identities: an instance event carries whoever
// asked for it, and a lifecycle event is the daemon reporting on its own state with nobody behind it.
builder.Services.AddSingleton(sp => new LeafLifecycle(
    sp.GetRequiredService<IEventJournalWriter>(),
    sp.GetRequiredService<ILogger<LeafLifecycle>>(),
    clock: null,
    startedAt: () => imageStartedAt));
builder.Services.AddSingleton<DesiredStateStore>();
// Inc 7 Phase 2 — companion supervision-state.json: persists restart counters / give-up latch so they
// survive ANY daemon death (OOM/SIGKILL), not just a planned hot-swap. Injected into InstanceSupervisor.
builder.Services.AddSingleton<SupervisionStateStore>();
// The run ledger: how each run ended, keyed by the console file's mtime so a consumer can find the
// run that holds a crash instead of guessing from timestamps.
builder.Services.AddSingleton<RunHistoryStore>();
// UPnP port forwarding: process-lifetime network state the supervisor owns (opens on bring-up,
// holds across crash-restart, closes on intended stop). Self-gates on enable_port_forwarding.
builder.Services.AddSingleton<UpnpService>();
// Host firewall: the same process-lifetime shape as UPnP on the other side of the door — UPnP opens the
// ROUTER, this opens the HOST. The authority (kgsm-firewall) still owns every firewall write; the
// supervisor only owns the trigger, because it is the only thing that sees a boot-autostart or a
// crash-respawn. Reached through kgsm-lib's client, like every other C# consumer of the authority.
builder.Services.AddKgsmFirewallClient(options.FirewallSocketPath);
builder.Services.AddSingleton<FirewallPortsService>();
builder.Services.AddSingleton<InstanceSupervisor>();

// The supervisor holds desired-state, so it is what can answer which forwarded ports are still claimed
// when one instance releases its own. Exposed as the narrow read-only query rather than the whole
// supervisor, so the container lifecycle ingester can ask without becoming a client of supervision.
builder.Services.AddSingleton<IForwardedPortClaims>(sp => sp.GetRequiredService<InstanceSupervisor>());

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

// The UPnP sweep: restores router forwards the IGD dropped underneath a running instance. Its own
// timer rather than a slow sub-cadence of the crash watcher, because a sweep is a multi-second network
// round trip and the supervision loop ticks at 1 Hz holding the gate. Needs no gate of its own — it
// reads the instance table lock-free, exactly as /status and /list do.
builder.Services.AddHostedService<UpnpReconciler>();

// Heap trimmer: hands free memory back to the OS — once after startup, then periodically when activity
// has grown the resident set and the daemon has settled again. A Workstation-GC daemon that allocates
// almost nothing at idle never triggers a GC on its own, so each burst of control-plane traffic ratchets
// RSS up and it stays up; this returns it (compacting gen-2 collect + malloc_trim), growth-gated so a
// genuinely idle daemon just ticks. Pure optimization, runs off the startup path.
builder.Services.AddHostedService<MemoryTrimmer>();

// Player-presence ingester: the watchdog's CONTAINER role. Tails each container instance's
// events/events.ndjson channel and re-emits player join/left as kgsm wire events (origin=system).
// Pure file-reader + forwarder — never shells docker, never supervises containers; additive to the
// native supervision above. Self-resolves the kgsm instances dir post-bootstrap.
builder.Services.AddHostedService<PlayerPresenceIngester>();

// Container lifecycle ingester: the watchdog's SECOND container role (a peer of
// PlayerPresenceIngester, tailing a different channel in the same bind-mounted /run/kgsm dir). Tails
// each container instance's events/lifecycle.ndjson (kgsm-containers Phase 1) and drives the SAME
// UpnpService singleton the native supervision path uses — open on instance_started, close on
// instance_stopping. UPnP-only: it does NOT emit kgsm wire events (the container's own manage.sh
// already does that). Never shells docker; only acts on container-runtime instances so native UPnP
// (already driven by InstanceSupervisor) is never double-driven.
builder.Services.AddHostedService<ContainerLifecycleIngester>();

// Player-presence + readiness ingester: the watchdog's NATIVE role. Tails each native instance's own
// game log (instance.LogFile, which SpawnEngine already targets) and emits (a) the SAME player
// join/left wire events (origin=system), matching the blueprint's player_*_regex with the pure
// NativeLogMatcher, and (b) instance-ready (Inc 9) — the "finished booting" signal, matching
// startup_success_regex with the pure NativeReadinessMatcher, keyed on the instance's cgroup
// populated-edge (reads CgroupManager — read-only, never acts). Additive + decoupled from
// InstanceSupervisor; no spawn-path change.
builder.Services.AddSingleton<PlayerNameStore>();
builder.Services.AddSingleton<PlayerSessionStore>();
builder.Services.AddHostedService<NativePlayerPresenceIngester>();

// RCON player-presence poller: polls game servers that support Source RCON for connected
// players, detecting leaves when the game server does not log disconnects. Additive to
// the native log-based ingester above — both write to the same PlayerSessionStore, and
// the store's dedup logic prevents double-counting.
builder.Services.AddHostedService<RconPlayerPresencePoller>();

// Control plane: a unix domain socket (no TCP port; the socket's filesystem perms are the
// security boundary, same model as kgsm-monitor).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    if (File.Exists(options.SocketPath))
        File.Delete(options.SocketPath);
    kestrel.ListenUnixSocket(options.SocketPath);
});

var app = builder.Build();

// Boot sequence runs BEFORE the socket binds (app.Run): under systemd delegation it discovers the
// daemon's own delegated cgroup base (/proc/self/cgroup), enters the supervisor leaf, and enables
// controllers on the base — no root step, no privilege drop (systemd User= runs us as the KGSM user;
// RuntimeDirectory= owns the socket dir). A failed bootstrap does not crash the daemon; it sets
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

    // Report readiness here rather than after the bootstrap call above, because both halves have to be
    // true: the bootstrap decided whether this daemon may spawn, and only now is the control socket
    // listening for somebody to ask it to. SupervisorState is the same answer /health serves, so the
    // journal and the probe cannot disagree about whether this daemon came up.
    SupervisorState supervisor = app.Services.GetRequiredService<SupervisorState>();
    LeafLifecycle lifecycle = app.Services.GetRequiredService<LeafLifecycle>();

    if (supervisor.Ready)
        lifecycle.MarkReady(supervisor.Detail);
    else
        lifecycle.MarkDegraded(WatchdogComponents.Delegation, supervisor.Detail);
});

// The last thing this daemon says. ⚠ A hot-swap has already said it with reason `reload` by the time
// this runs, and MarkStopping only writes once — so a swap that keeps every supervised game running is
// never reported as a stop, which is the distinction a consumer needs and cannot recover afterwards.
app.Lifetime.ApplicationStopping.Register(() =>
    app.Services.GetRequiredService<LeafLifecycle>().MarkStopping(LeafStopReason.Signal));

app.MapWatchdog();
app.MapConsole();

// Surface typo'd config: a misspelled Watchdog__* var silently falls back to its default
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
