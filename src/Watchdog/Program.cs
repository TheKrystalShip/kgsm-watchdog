using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Control;
using TheKrystalShip.KGSM.Watchdog.Supervision;

var options = WatchdogOptions.FromEnvironment();

// Unlike the monitor, the watchdog cannot run "headless" without KGSM: it reads each instance's
// spawn config via kgsm-lib before forking it. No path, nothing to do — fail fast and loud.
if (string.IsNullOrEmpty(options.KgsmPath))
{
    Console.Error.WriteLine("FATAL: KGSM_WATCHDOG_KGSM_PATH is required (absolute path to kgsm.sh).");
    return 1;
}

var builder = WebApplication.CreateSlimBuilder(args);

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
builder.Services.AddSingleton<InstanceSupervisor>();

// The crash watcher: polls each instance's cgroup.events and drives restart-with-backoff. It is the
// clock; the supervisor holds all the state and makes all the decisions (one decision point).
builder.Services.AddHostedService<CrashWatcher>();

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

var ready = app.Services.GetRequiredService<SupervisorState>();
app.Logger.LogInformation(
    "kgsm-watchdog listening on unix:{Socket} — supervisor ready={Ready} ({Detail})",
    options.SocketPath, ready.Ready, ready.Detail);

app.Run();
return 0;
