using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the hot-swap coordinator's SAFETY-GATE DECISION (Inc 7 / Option 3) WITHOUT ever executing a
/// real swap: the actual quiesce+produce+execv step is injected, so the test substitutes a recording
/// stub and asserts whether it was REACHED. The whole point of the gate is that a broken new binary
/// (<c>--selfcheck</c> ≠ 0) must NOT take the supervised fleet down — so a failing self-check aborts
/// before the swap is attempted, and a passing one proceeds to it. Never calls <c>ReExec.Exec</c>, never
/// raises a real signal, never marshals/execs.
/// <para>
/// Also covers the CONSUMER-side robustness of <see cref="InstanceSupervisor.AdoptFromHandoff"/> that
/// needs no exec: an absent handoff is a no-op (a plain start), and a malformed blob degrades to the
/// normal restore instead of wedging boot, always clearing the env var afterwards.
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // AdoptFromHandoff tests mutate the handoff env var — serialize them
public sealed class HotSwapCoordinatorTests
{
    [Fact]
    public async Task Failing_selfcheck_aborts_without_attempting_a_swap()
    {
        var events = new RecordingEvents();
        var supervisor = NewSupervisor(events);

        bool swapAttempted = false;
        var coordinator = new HotSwapCoordinator(
            supervisor,
            NullLogger<HotSwapCoordinator>.Instance,
            selfcheckRunner: _ => 1,                       // the new binary fails its self-check
            executeSwap: _ => { swapAttempted = true; return false; });

        await coordinator.TriggerAsync();

        Assert.False(swapAttempted, "a failing --selfcheck must ABORT before the swap is attempted");
    }

    [Fact]
    public async Task Timed_out_selfcheck_sentinel_also_aborts()
    {
        var supervisor = NewSupervisor(new RecordingEvents());
        bool swapAttempted = false;
        var coordinator = new HotSwapCoordinator(
            supervisor,
            NullLogger<HotSwapCoordinator>.Instance,
            selfcheckRunner: _ => -2,                      // the real runner's timeout sentinel
            executeSwap: _ => { swapAttempted = true; return false; });

        await coordinator.TriggerAsync();

        Assert.False(swapAttempted, "a timed-out --selfcheck (non-zero) must also abort");
    }

    [Fact]
    public async Task Passing_selfcheck_proceeds_to_the_swap_step()
    {
        var supervisor = NewSupervisor(new RecordingEvents());
        string? swappedTarget = null;
        var coordinator = new HotSwapCoordinator(
            supervisor,
            NullLogger<HotSwapCoordinator>.Instance,
            selfcheckRunner: _ => 0,                       // the new binary passes its self-check
            executeSwap: target => { swappedTarget = target; return false; }); // stub returns instead of exec'ing

        await coordinator.TriggerAsync();

        Assert.NotNull(swappedTarget); // the produce/exec step WAS reached on a clean gate
    }

    [Fact]
    public async Task A_throwing_selfcheck_is_treated_as_a_failure_and_aborts()
    {
        var supervisor = NewSupervisor(new RecordingEvents());
        bool swapAttempted = false;
        var coordinator = new HotSwapCoordinator(
            supervisor,
            NullLogger<HotSwapCoordinator>.Instance,
            selfcheckRunner: _ => throw new InvalidOperationException("probe blew up"),
            executeSwap: _ => { swapAttempted = true; return false; });

        // Must not propagate — the trigger swallows the gate failure and aborts.
        await coordinator.TriggerAsync();

        Assert.False(swapAttempted, "a self-check that throws is a failed gate — abort, never swap");
    }

    [Fact]
    public async Task A_concurrent_swap_in_progress_is_collapsed_to_one()
    {
        var supervisor = NewSupervisor(new RecordingEvents());

        int swapCalls = 0;
        var gate = new ManualResetEventSlim(false);
        var coordinator = new HotSwapCoordinator(
            supervisor,
            NullLogger<HotSwapCoordinator>.Instance,
            selfcheckRunner: _ => 0,
            executeSwap: _ =>
            {
                Interlocked.Increment(ref swapCalls);
                gate.Wait(TimeSpan.FromSeconds(2)); // hold the "swap" open so the 2nd trigger sees in-progress
                return false;
            });

        var first = Task.Run(async () => await coordinator.TriggerAsync());
        // Give the first one time to enter the swap step and hold the in-progress flag.
        SpinWait.SpinUntil(() => Volatile.Read(ref swapCalls) == 1, TimeSpan.FromSeconds(2));

        // A second trigger while the first is "swapping" must be ignored (not a second swap).
        await coordinator.TriggerAsync();

        gate.Set();
        await first;

        Assert.Equal(1, swapCalls); // the concurrent SIGHUP was collapsed
    }

    // ---- consumer-side robustness: AdoptFromHandoff without an exec ----------------------------------

    [Fact]
    public void AdoptFromHandoff_is_a_noop_when_the_handoff_env_var_is_absent()
    {
        string? prior = Environment.GetEnvironmentVariable(Model.HotSwapHandoff.EnvVarName);
        Environment.SetEnvironmentVariable(Model.HotSwapHandoff.EnvVarName, null);
        try
        {
            var supervisor = NewSupervisor(new RecordingEvents());
            supervisor.AdoptFromHandoff();                  // must not throw — a plain (non-swap) start
            Assert.Empty(supervisor.List());                // nothing adopted
        }
        finally { Environment.SetEnvironmentVariable(Model.HotSwapHandoff.EnvVarName, prior); }
    }

    [Fact]
    public void AdoptFromHandoff_degrades_on_a_malformed_blob_and_clears_the_env_var()
    {
        string? prior = Environment.GetEnvironmentVariable(Model.HotSwapHandoff.EnvVarName);
        Environment.SetEnvironmentVariable(Model.HotSwapHandoff.EnvVarName, "!!! not base64 !!!");
        try
        {
            var supervisor = NewSupervisor(new RecordingEvents());
            supervisor.AdoptFromHandoff();                  // must not throw
            Assert.Empty(supervisor.List());                // nothing adopted from a bad blob
            // ALWAYS cleared, so a later plain restart can never re-read the bad blob.
            Assert.Null(Environment.GetEnvironmentVariable(Model.HotSwapHandoff.EnvVarName));
        }
        finally { Environment.SetEnvironmentVariable(Model.HotSwapHandoff.EnvVarName, prior); }
    }

    // ---- minimal real InstanceSupervisor (the coordinator takes the concrete type) -------------------

    private static InstanceSupervisor NewSupervisor(RecordingEvents events)
    {
        var options = new WatchdogOptions
        {
            // A private temp state dir so PersistSupervisionState (if ever reached) never touches a real HOME.
            StateFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-coord-{Guid.NewGuid():N}", "desired-state.json"),
        };
        var cgroups = new CgroupManager(options, NullLogger<CgroupManager>.Instance);
        var spawn = new SpawnEngine(cgroups, NullLogger<SpawnEngine>.Instance);
        var state = new SupervisorState { Ready = true, Detail = "test" };
        return new InstanceSupervisor(
            new EmptyInstances(),
            spawn,
            cgroups,
            new BackoffPolicy(),
            state,
            new DesiredStateStore(options, NullLogger<DesiredStateStore>.Instance),
            new SupervisionStateStore(options, NullLogger<SupervisionStateStore>.Instance),
            events,
            new UpnpService(NullLogger<UpnpService>.Instance),
            NullLogger<InstanceSupervisor>.Instance);
    }

    /// <summary>An IInstanceService that knows about no instances — enough for the gate-decision tests.</summary>
    private sealed class EmptyInstances : IInstanceService
    {
        public Instance? GetInstanceInfo(string instanceName) => null;
        public Dictionary<string, Instance> GetAll() => new();
        public Dictionary<string, Instance>? GetAllOrNull() => new();

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
        public KgsmResult CheckUpdate(string instanceName) => throw new NotImplementedException();
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
    }

    /// <summary>Records EmitWithProvenance calls (the abort path emits a system event through here).</summary>
    private sealed class RecordingEvents : IEventManagementService
    {
        public List<string> Emitted { get; } = [];
        public KgsmResult EmitWithProvenance(string eventType, string? actor, string? origin, params string[] parameters)
        {
            Emitted.Add(eventType);
            return new KgsmResult(0);
        }
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
