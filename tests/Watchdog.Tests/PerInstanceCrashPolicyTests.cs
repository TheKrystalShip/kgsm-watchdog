using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers what the reconcile pass decides when a supervised instance's cgroup has emptied — the two
/// things that can override a plain "crashed → restart":
/// <list type="bullet">
/// <item>the per-instance crash policy overlay (kgsm-lib <c>Instance.CrashRestart</c> /
/// <c>CrashMaxRestarts</c>): the global <see cref="BackoffPolicy"/> singleton stays, but at
/// crash-detection time <c>crash_restart=false</c> suppresses auto-recovery and <c>crash_max_restarts</c>
/// overrides the give-up ceiling for that one instance;</item>
/// <item>the stop-intent gate: <c>DesiredRunning=false</c> means the exit is a stop completing, so it is
/// never classified as a crash at all.</item>
/// </list>
/// <para>
/// A supervised instance is injected in the <c>Running</c> phase (with a controllable
/// <c>SpawnedAt</c> and seeded restart streak) via the hot-swap handoff adoption path — the one public
/// door into the state table that does not fork a real game. Its cgroup is never populated (the
/// <see cref="CgroupManager"/> base points at an empty temp dir), so a single <c>Reconcile()</c> pass
/// takes the "it exited" branch and exercises exactly the new overlay logic — no real process, no
/// second respawn (a genuine second crash would route through <c>ReconcileRestartPending</c>'s real
/// fork; the streak is instead pre-seeded through the handoff to represent the prior restart).
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // adoption reads/writes the handoff env var — serialize with other env mutators
public sealed class PerInstanceCrashPolicyTests
{
    [Fact]
    public void CrashRestart_false_stops_without_a_retry_and_emits_crashed()
    {
        var events = new RecordingEvents();
        var spec = SpecFor("no-recover", crashRestart: false);
        var supervisor = NewSupervisor(events, spec);

        AdoptRunning(supervisor, spec.Name, spawnedAt: Old(), consecutiveFailures: 0);
        supervisor.Reconcile();

        var state = Single(supervisor);
        Assert.Equal("stopped", state.Phase);                 // no auto-recovery
        Assert.Equal(0, state.Restarts);                   // no retry slot consumed
        Assert.True(events.WaitFor(EventCrashed), "crash_restart=false must still emit instance-crashed for alert visibility");
        Assert.DoesNotContain("instance-restarted", events.Snapshot());
    }

    [Fact]
    public void CrashMaxRestarts_grants_a_retry_below_the_override_ceiling()
    {
        var events = new RecordingEvents();
        var spec = SpecFor("cap-one", crashMaxRestarts: 1);
        var supervisor = NewSupervisor(events, spec);

        // First crash of the episode (streak 0 → 1): still within the per-instance ceiling of 1 → retry.
        AdoptRunning(supervisor, spec.Name, spawnedAt: Old(), consecutiveFailures: 0);
        supervisor.Reconcile();

        var state = Single(supervisor);
        Assert.Equal("restart-pending", state.Phase);
        Assert.Equal(1, state.Restarts);
        Assert.True(events.WaitFor(EventCrashed));
    }

    [Fact]
    public void CrashMaxRestarts_override_gives_up_at_a_lower_ceiling_than_the_global()
    {
        var events = new RecordingEvents();
        var spec = SpecFor("cap-one", crashMaxRestarts: 1);
        var supervisor = NewSupervisor(events, spec);

        // Seed the streak at 1 (one restart already spent), then crash again: 2 > the override ceiling
        // of 1 → give up. The global default (5) would NOT give up here, so Failed proves the override.
        AdoptRunning(supervisor, spec.Name, spawnedAt: Old(), consecutiveFailures: 1);
        supervisor.Reconcile();

        var state = Single(supervisor);
        Assert.Equal("failed", state.Phase);
        Assert.Equal(2, state.Restarts);
        Assert.True(events.WaitFor(EventFailed), "hitting the per-instance ceiling must emit instance-failed");
    }

    [Fact]
    public void No_crash_config_falls_back_to_the_global_ceiling()
    {
        var events = new RecordingEvents();
        var spec = SpecFor("defaults"); // CrashRestart null → auto-restart on; CrashMaxRestarts null → global 5
        var supervisor = NewSupervisor(events, spec);

        // Same seeded streak (1) as the give-up test, but with NO override the global ceiling (5) still
        // allows a retry — the contrast that pins the give-up in the prior test to the override alone.
        AdoptRunning(supervisor, spec.Name, spawnedAt: Old(), consecutiveFailures: 1);
        supervisor.Reconcile();

        var state = Single(supervisor);
        Assert.Equal("restart-pending", state.Phase);
        Assert.Equal(2, state.Restarts);
    }

    // ---- the stop-intent gate ------------------------------------------------------------------
    // Intent outranks detection: a record whose DesiredRunning is false describes an instance an
    // operator asked to stop, so its exit is that stop completing — never a crash. Reaches the same
    // "it exited" reconcile branch as the tests above (cgroup never populated), and must take the
    // opposite decision.

    [Fact]
    public void Stop_intent_completes_the_teardown_instead_of_restarting()
    {
        var events = new RecordingEvents();
        var spec = SpecFor("stopping");
        var supervisor = NewSupervisor(events, spec);

        AdoptRunning(supervisor, spec.Name, spawnedAt: Old(), consecutiveFailures: 0, desiredRunning: false);
        supervisor.Reconcile();

        // Dropped from the table exactly as a completed stop would leave it — nothing left to restart.
        Assert.Empty(supervisor.List());
        Assert.DoesNotContain(EventCrashed, events.Snapshot());
        Assert.DoesNotContain("instance-restarted", events.Snapshot());
    }

    [Fact]
    public void Stop_intent_does_not_consume_a_retry_slot_or_give_up()
    {
        var events = new RecordingEvents();
        var spec = SpecFor("stopping-mid-streak");
        var supervisor = NewSupervisor(events, spec);

        // A stop landing on an instance that had already crashed once must not register a second
        // failure — the exit is the operator's, not the game's.
        AdoptRunning(supervisor, spec.Name, spawnedAt: Old(), consecutiveFailures: 1, desiredRunning: false);
        supervisor.Reconcile();

        Assert.Empty(supervisor.List());
        Assert.DoesNotContain(EventFailed, events.Snapshot());
    }

    // ---- helpers -------------------------------------------------------------------------------

    private const string EventCrashed = "instance-crashed";
    private const string EventFailed = "instance-failed";

    private static DateTime Old() => DateTime.UtcNow - TimeSpan.FromMinutes(5); // past both grace + stability windows

    private static InstanceState Single(InstanceSupervisor supervisor)
    {
        var all = supervisor.List();
        return Assert.Single(all);
    }

    /// <summary>
    /// Inject one instance into the supervisor's table in the <c>Running</c> phase via the hot-swap
    /// handoff adoption path. The inherited fd is deliberately invalid and the spec's FIFO does not
    /// exist, so adoption lands on cgroup-only supervision (<c>Current == null</c>) — exactly the shape a
    /// crash-detection pass needs, with no real process.
    /// </summary>
    private static void AdoptRunning(
        InstanceSupervisor supervisor, string name, DateTime spawnedAt, int consecutiveFailures, bool desiredRunning = true)
    {
        var handoff = new HotSwapHandoff
        {
            Instances =
            {
                new HotSwapEntry
                {
                    Name = name,
                    FifoFd = -1,                 // invalid → no inherited-fd adopt; FIFO re-open then fails → cgroup-only
                    FifoPath = "",
                    ConsecutiveFailures = consecutiveFailures,
                    GaveUp = false,
                    Phase = nameof(SupervisionPhase.Running),
                    SpawnedAt = spawnedAt,
                    DesiredRunning = desiredRunning,
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

    private static Instance SpecFor(string name, bool? crashRestart = null, int? crashMaxRestarts = null) => new()
    {
        Name = name,
        SocketFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-nofifo-{Guid.NewGuid():N}.fifo"), // never exists → cgroup-only adopt
        CrashRestart = crashRestart,
        CrashMaxRestarts = crashMaxRestarts,
    };

    private static InstanceSupervisor NewSupervisor(RecordingEvents events, Instance spec)
    {
        var options = new WatchdogOptions
        {
            // Empty temp cgroup base → no instance cgroup ever populated → the reconcile pass sees "exited".
            CgroupMountPoint = Path.Combine(Path.GetTempPath(), $"kgsm-wd-cg-{Guid.NewGuid():N}"),
            StateFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-crash-{Guid.NewGuid():N}", "desired-state.json"),
        };
        var cgroups = new CgroupManager(options, NullLogger<CgroupManager>.Instance);
        var spawn = new SpawnEngine(cgroups, NullLogger<SpawnEngine>.Instance);
        var state = new SupervisorState { Ready = true, Detail = "test" };
        return new InstanceSupervisor(
            new SingleInstance(spec),
            spawn,
            cgroups,
            new BackoffPolicy(), // global default: MaxRetries=5, GraceWindow=10s
            state,
            new DesiredStateStore(options, NullLogger<DesiredStateStore>.Instance),
            new SupervisionStateStore(options, NullLogger<SupervisionStateStore>.Instance),
            events,
            new UpnpService(NullLogger<UpnpService>.Instance),
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
    
    public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
}

    /// <summary>Thread-safe recorder — supervision events are emitted fire-and-forget on the thread pool.</summary>
    private sealed class RecordingEvents : IEventManagementService
    {
        private readonly object _lock = new();
        private readonly List<string> _emitted = [];

        public KgsmResult EmitWithProvenance(string eventType, string? actor, string? origin, params string[] parameters)
        {
            lock (_lock) _emitted.Add(eventType);
            return new KgsmResult(0);
        }

        public string[] Snapshot() { lock (_lock) return _emitted.ToArray(); }

        public bool WaitFor(string eventType, int timeoutMs = 2000)
            => SpinWait.SpinUntil(() => Snapshot().Contains(eventType), TimeSpan.FromMilliseconds(timeoutMs));

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
