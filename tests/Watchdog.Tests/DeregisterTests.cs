using System.Text;
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
/// Covers <see cref="InstanceSupervisor.ForgetAsync"/> — the deregistration verb behind
/// <c>DELETE /instance/{name}</c>, called by <c>kgsm uninstall</c>. It exists because an instance that
/// no longer exists must stop being supervised: without it the daemon keeps a <c>desired=running</c>
/// record forever, restart-loops a game whose install directory is gone, and feeds a permanent,
/// unresolvable condition to every consumer of <c>/list</c>.
/// <para>
/// The supervised instance is injected through the hot-swap handoff adoption path (the one public door
/// into the state table that does not fork a real game), with the cgroup base pointing at an empty temp
/// dir so nothing is ever "populated" — the live-process teardown belongs to on-host validation, not
/// unit tests. What is asserted here is the bookkeeping: table entry, boot-autostart intent, and the
/// honesty of the reported outcome.
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // adoption reads/writes the handoff env var — serialize with other env mutators
public sealed class DeregisterTests
{
    [Fact]
    public async Task Forget_removes_a_supervised_instance_from_the_table()
    {
        var spec = SpecFor("wd-forget");
        var supervisor = NewSupervisor(spec);
        AdoptRunning(supervisor, spec.Name);
        Assert.Single(supervisor.List());

        var result = await supervisor.ForgetAsync(spec.Name);

        Assert.True(result.Ok);
        Assert.Empty(supervisor.List());
    }

    [Fact]
    public async Task Forget_reports_it_was_supervised_even_though_the_stop_clears_the_entry_first()
    {
        // Regression guard: ForgetAsync stops the instance before its own bookkeeping, and StopAsync
        // removes the table entry on its success paths. Sampling "was it tracked?" AFTER the stop would
        // report "not supervised" for an instance we just tore down — a plausible-sounding lie in the
        // operator's log, exactly the kind this project refuses to emit.
        var spec = SpecFor("wd-forget-msg");
        var supervisor = NewSupervisor(spec);
        AdoptRunning(supervisor, spec.Name);

        var result = await supervisor.ForgetAsync(spec.Name);

        Assert.True(result.Ok);
        Assert.Equal("deregistered", result.Message);
    }

    [Fact]
    public async Task Forget_is_an_idempotent_noop_for_an_instance_that_was_never_supervised()
    {
        // The caller's kgsm spec is normally already deleted by the time it deregisters, so an unknown
        // name must succeed — an uninstall can never fail because the daemon had already forgotten it.
        var supervisor = NewSupervisor(SpecFor("wd-present"));

        var result = await supervisor.ForgetAsync("wd-never-existed");

        Assert.True(result.Ok);
        Assert.Equal("not supervised (nothing to deregister)", result.Message);
    }

    [Fact]
    public async Task Forget_drops_the_boot_autostart_intent()
    {
        // The boot axis is separate from the runtime one, so forgetting the table entry alone would let
        // RestoreAsync resurrect a deleted instance on the next daemon start.
        var spec = SpecFor("wd-forget-enabled");
        var supervisor = NewSupervisor(spec);
        await supervisor.EnableAsync(spec.Name);
        Assert.Equal([spec.Name], supervisor.EnabledNames());

        await supervisor.ForgetAsync(spec.Name);

        Assert.Empty(supervisor.EnabledNames());
    }

    // ---- harness ---------------------------------------------------------------------------------

    /// <summary>
    /// Inject one instance into the supervisor's table in the <c>Running</c> phase via the hot-swap
    /// handoff adoption path — the same door <see cref="PerInstanceCrashPolicyTests"/> uses. The
    /// inherited fd is deliberately invalid and the spec's FIFO never exists, so adoption lands
    /// cgroup-only with no real process behind it.
    /// </summary>
    private static void AdoptRunning(InstanceSupervisor supervisor, string name)
    {
        var handoff = new HotSwapHandoff
        {
            Instances =
            {
                new HotSwapEntry
                {
                    Name = name,
                    FifoFd = -1,
                    FifoPath = "",
                    ConsecutiveFailures = 0,
                    GaveUp = false,
                    Phase = nameof(SupervisionPhase.Running),
                    SpawnedAt = DateTime.UtcNow.AddMinutes(-5),
                    DesiredRunning = true,
                },
            },
        };
        string json = JsonSerializer.Serialize(handoff, WatchdogJsonContext.Default.HotSwapHandoff);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        string? prior = Environment.GetEnvironmentVariable(HotSwapHandoff.EnvVarName);
        Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, b64);
        try { supervisor.AdoptFromHandoff(); } // clears the env var itself on the way out
        finally { Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, prior); }
    }

    private static Instance SpecFor(string name) => new()
    {
        Name = name,
        SocketFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-nofifo-{Guid.NewGuid():N}.fifo"),
    };

    private static InstanceSupervisor NewSupervisor(Instance spec)
    {
        var options = new WatchdogOptions
        {
            // Empty temp cgroup base → no instance cgroup is ever populated.
            CgroupMountPoint = Path.Combine(Path.GetTempPath(), $"kgsm-wd-cg-{Guid.NewGuid():N}"),
            StateFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-forget-{Guid.NewGuid():N}", "desired-state.json"),
        };
        var cgroups = new CgroupManager(options, NullLogger<CgroupManager>.Instance);
        var spawn = new SpawnEngine(cgroups, NullLogger<SpawnEngine>.Instance);
        var state = new SupervisorState { Ready = true, Detail = "test" };
        return new InstanceSupervisor(
            new SingleInstance(spec),
            spawn,
            cgroups,
            new BackoffPolicy(),
            state,
            new DesiredStateStore(options, NullLogger<DesiredStateStore>.Instance),
            new SupervisionStateStore(options, NullLogger<SupervisionStateStore>.Instance),
            new RecordingEvents(),
            new UpnpService(NullLogger<UpnpService>.Instance),
            new FirewallPortsService(
                new FirewallPortsServiceTests.FakeFirewall(), NullLogger<FirewallPortsService>.Instance),
            new PlayerSessionStore(),
            NullLogger<InstanceSupervisor>.Instance);
    }

    /// <summary>A no-op event sink — these tests assert supervisor state, not emitted events.</summary>
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
}
