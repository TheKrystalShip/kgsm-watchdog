using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Firewall;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Guards the spawn/adopt distinction the log-rotation fix (PLAN.md Increment 9 follow-up,
/// <see cref="SpawnEngine.RotateLogFile"/>) depends on for correctness: rotation must run ONLY on a
/// genuine fresh spawn, NEVER on adopt (a daemon restart/hot-swap re-attaching to a game that is still
/// running and still writing its own log) — truncating or moving that log out from under a live writer
/// would destroy in-flight output. <see cref="InstanceSupervisor.AdoptFromHandoff"/> (same-PID hot-swap
/// re-exec) and the plain re-adopt-a-live-orphan path (<see cref="InstanceSupervisor.AdoptLiveOrphansAsync"/>,
/// a daemon COLD restart re-attaching to a survivor) are the two adopt entry points; neither calls
/// <see cref="SpawnEngine.Spawn"/> (the sole caller of <c>RotateLogFile</c>) — this is a structural
/// guarantee (grep confirms <c>InstanceSupervisor.TrySpawn</c> is the ONLY caller of <c>Spawn</c>, and
/// TrySpawn is called only from <c>StartAsync</c>/<c>RespawnFresh</c>/<c>ReconcileRestartPending</c>,
/// never from either adopt path). These tests prove the OBSERVABLE consequence: a real log file with
/// real content survives both adopt paths byte-for-byte, at the same inode.
/// </summary>
[Collection(EnvironmentCollection.Name)] // AdoptFromHandoff mutates the handoff env var — serialize with other env mutators
public sealed class AdoptDoesNotRotateLogTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "kgsm-wd-adopt-nolog-" + Guid.NewGuid().ToString("N"));

    public AdoptDoesNotRotateLogTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void HotSwap_AdoptFromHandoff_never_touches_the_instance_log()
    {
        const string content = "run in progress...\nplayer joined\n";
        string log = Path.Combine(_tempDir, "hotswap-survivor.log");
        File.WriteAllText(log, content);
        ulong? inodeBefore = EventChannelTail.TryReadInode(log);

        var spec = new Instance
        {
            Name = "hotswap-survivor",
            Runtime = InstanceRuntime.Native,
            LogFile = log,
            SocketFile = Path.Combine(_tempDir, "never-exists.fifo"), // ReopenFifo fails -> cgroup-only adopt
        };
        var events = new RecordingEvents();
        var supervisor = NewSupervisor(events, spec);

        var handoff = new HotSwapHandoff
        {
            Instances =
            {
                new HotSwapEntry
                {
                    Name = spec.Name,
                    FifoFd = -1,     // invalid -> falls back to ReopenFifo (also fails) -> cgroup-only
                    FifoPath = "",
                    ConsecutiveFailures = 0,
                    GaveUp = false,
                    Phase = nameof(SupervisionPhase.Running),
                    SpawnedAt = DateTime.UtcNow,
                    DesiredRunning = true,
                },
            },
        };
        string json = JsonSerializer.Serialize(handoff, WatchdogJsonContext.Default.HotSwapHandoff);
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        string? prior = Environment.GetEnvironmentVariable(HotSwapHandoff.EnvVarName);
        Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, b64);
        try { supervisor.AdoptFromHandoff(); }
        finally { Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, prior); }

        Assert.Single(supervisor.List()); // the instance WAS adopted (proves the path actually ran)
        Assert.True(File.Exists(log), "hot-swap adopt must never remove the live game's log");
        Assert.Equal(content, File.ReadAllText(log)); // byte-for-byte unchanged
        Assert.Equal(inodeBefore, EventChannelTail.TryReadInode(log)); // SAME inode — never rotated
    }

    [Fact]
    public async Task Cold_restart_AdoptLiveOrphansAsync_never_touches_the_instance_log()
    {
        const string content = "still running across the daemon bounce\n";
        string log = Path.Combine(_tempDir, "orphan-survivor.log");
        File.WriteAllText(log, content);
        ulong? inodeBefore = EventChannelTail.TryReadInode(log);

        var spec = new Instance
        {
            Name = "orphan-survivor",
            Runtime = InstanceRuntime.Native,
            LogFile = log,
            SocketFile = Path.Combine(_tempDir, "never-exists.fifo"),
        };
        var events = new RecordingEvents();
        var supervisor = NewSupervisor(events, spec, out var cgroups);

        // The instance's cgroup is already populated (it outlived the restart) but NOT in the
        // persisted boot-autostart set — exactly the "started-not-enabled survivor" AdoptLiveOrphansAsync
        // exists for.
        SetPopulated(cgroups, spec.Name, populated: true);

        await supervisor.AdoptLiveOrphansAsync();

        Assert.Single(supervisor.List()); // re-adopted
        Assert.True(File.Exists(log), "cold-restart re-adopt must never remove the live game's log");
        Assert.Equal(content, File.ReadAllText(log));
        Assert.Equal(inodeBefore, EventChannelTail.TryReadInode(log));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private void SetPopulated(CgroupManager cgroups, string instance, bool populated)
    {
        string dir = cgroups.PathFor(instance);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cgroup.events"), $"populated {(populated ? 1 : 0)}\nfrozen 0\n");
    }

    private InstanceSupervisor NewSupervisor(RecordingEvents events, Instance spec)
        => NewSupervisor(events, spec, out _);

    private InstanceSupervisor NewSupervisor(RecordingEvents events, Instance spec, out CgroupManager cgroups)
    {
        var options = new WatchdogOptions
        {
            CgroupMountPoint = Path.Combine(_tempDir, "cg"),
            CgroupBaseName = "kgsm.slice",
            StateFile = Path.Combine(_tempDir, "state", "desired-state.json"),
        };
        cgroups = new CgroupManager(options, NullLogger<CgroupManager>.Instance);
        var spawn = new SpawnEngine(cgroups, NullLogger<SpawnEngine>.Instance);
        var state = new SupervisorState { Ready = true, Detail = "test" };
        return new InstanceSupervisor(
            new SingleInstance(spec),
            spawn,
            cgroups,
            new BackoffPolicy(),
            state,
            TestState.Desired(options),
            TestState.Supervision(options),
            TestState.RunHistory(options),
            events,
            new UpnpService(NullLogger<UpnpService>.Instance),
            new FirewallPortsService(
                new FirewallPortsServiceTests.FakeFirewall(), NullLogger<FirewallPortsService>.Instance),
            new PlayerSessionStore(),
            NullLogger<InstanceSupervisor>.Instance);
    }

    /// <summary>An IInstanceService that returns exactly one configured spec by name.</summary>
    private sealed class SingleInstance(Instance instance) : IInstanceService
    {
        public Instance? GetInstanceInfo(string instanceName) =>
            string.Equals(instanceName, instance.Name, StringComparison.Ordinal) ? instance : null;
        public Dictionary<string, Instance> GetAll() => new() { [instance.Name] = instance };
        public Dictionary<string, Instance>? GetAllOrNull() => GetAll();

        // ---- unused by these tests ----
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null, bool? start = null) => throw new NotImplementedException();
        public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
        public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
        public bool IsActive(string instanceName) => throw new NotImplementedException();
        public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetInstalledVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetLatestVersion(string instanceName) => throw new NotImplementedException();
        public KgsmResult CheckUpdate(string instanceName, bool emit = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult DeleteBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
    public List<InstanceBackup> GetBackupsDetailed(string instanceName) => throw new NotImplementedException();
    public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    
    public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
}

    /// <summary>Records EmitWithProvenance calls; not asserted on here, just needs to exist.</summary>
    private sealed class RecordingEvents : IEventManagementService
    {
        public KgsmResult EmitWithProvenance(string eventType, string? actor, string? origin, params string[] parameters) => new(0);
        public KgsmResult Emit(string eventType, params string[] parameters) => new(0);
        public KgsmResult GetStatus() => new(0);
        public KgsmResult TestTransport(string transport) => new(0);
        public KgsmResult EnableSocket() => new(0);
        public KgsmResult DisableSocket() => new(0);
        public KgsmResult TestSocket() => new(0);
        public KgsmResult GetSocketStatus() => new(0);
        public KgsmResult EnableWebhook() => new(0);
        public KgsmResult DisableWebhook() => new(0);
        public KgsmResult TestWebhook() => new(0);
        public KgsmResult GetWebhookStatus() => new(0);
    }
}
