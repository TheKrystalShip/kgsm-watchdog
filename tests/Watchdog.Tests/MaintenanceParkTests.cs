using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Events;
using TheKrystalShip.KGSM.Watchdog.Firewall;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The maintenance park: the lifecycle transition a leaf runs a multi-minute disruptive sequence
/// behind. What makes it a park rather than a stop is what it leaves alone — desired-state stays
/// <c>running</c>, the failure streak and the give-up latch stay where they were, and crash-restart is
/// suppressed by the phase rather than switched off.
/// <para>
/// The instance's cgroup is a directory of the same control files the kernel exposes, written by the
/// test: <c>cgroup.events</c> is what liveness is read from, so a background write of
/// <c>populated 0</c> is a game exiting. That covers everything except the spawn itself, which forks a
/// real process — so the tests that would need one assert the state the failed spawn leaves instead,
/// and a successful respawn is proven against a live instance.
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // the injection path reads/writes the handoff env var
public sealed class MaintenanceParkTests
{
    [Fact]
    public async Task Park_drains_the_instance_and_leaves_it_wanted_running()
    {
        var events = new RecordingJournal();
        var spec = SpecFor("parked");
        var supervisor = NewSupervisor(events, spec, out string cgroup);
        Adopt(supervisor, spec.Name, consecutiveFailures: 0);
        LiveCgroup(cgroup, spec.Name);

        var result = await supervisor.BeginMaintenanceAsync(spec.Name, "scheduler");

        Assert.True(result.Ok, result.Message);
        var state = Single(supervisor);
        Assert.Equal("maintenance", state.Phase);
        Assert.Equal("running", state.Desired);   // the park is not a stop
        Assert.False(state.Populated);
    }

    [Fact]
    public async Task Park_is_attributed_to_the_leaf_that_asked()
    {
        var events = new RecordingJournal();
        var spec = SpecFor("attributed");
        var supervisor = NewSupervisor(events, spec, out string cgroup);
        Adopt(supervisor, spec.Name, consecutiveFailures: 0);
        LiveCgroup(cgroup, spec.Name);

        await supervisor.BeginMaintenanceAsync(spec.Name, "scheduler");

        var recorded = Assert.Single(events.Recorded);
        Assert.Equal("server.restart.stopped", recorded.Type);
        // An unprefixed actor reads downstream as a person on the local host, which is what a leaf's
        // own action must never be recorded as.
        Assert.Equal("system:scheduler", recorded.Actor);
    }

    [Fact]
    public async Task A_parked_instance_is_not_crash_restarted()
    {
        var events = new RecordingJournal();
        var spec = SpecFor("suppressed");
        var supervisor = NewSupervisor(events, spec, out string cgroup);
        Adopt(supervisor, spec.Name, consecutiveFailures: 2);
        LiveCgroup(cgroup, spec.Name);

        await supervisor.BeginMaintenanceAsync(spec.Name, "scheduler");
        for (int i = 0; i < 3; i++)
            supervisor.Reconcile();

        // Desired-running with an empty cgroup is exactly the shape a crash has; the phase is what
        // tells the two apart.
        var state = Single(supervisor);
        Assert.Equal("maintenance", state.Phase);
        Assert.Equal(2, state.Restarts);                       // the streak the park found
        Assert.DoesNotContain("server.crashed", events.Snapshot());
        Assert.DoesNotContain("server.restarted", events.Snapshot());
    }

    [Fact]
    public async Task Park_refuses_a_container_instance()
    {
        var events = new RecordingJournal();
        var spec = SpecFor("in-docker");
        spec.Runtime = InstanceRuntime.Container;
        var supervisor = NewSupervisor(events, spec, out _);

        var result = await supervisor.BeginMaintenanceAsync(spec.Name, "scheduler");

        Assert.False(result.Ok);
        Assert.Contains("out of scope", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Park_refuses_an_instance_that_is_already_parked()
    {
        var events = new RecordingJournal();
        var spec = SpecFor("twice");
        var supervisor = NewSupervisor(events, spec, out string cgroup);
        Adopt(supervisor, spec.Name, consecutiveFailures: 0);
        LiveCgroup(cgroup, spec.Name);

        await supervisor.BeginMaintenanceAsync(spec.Name, "scheduler");
        var second = await supervisor.BeginMaintenanceAsync(spec.Name, "scheduler");

        Assert.False(second.Ok);
        Assert.Contains("already parked", second.Message, StringComparison.Ordinal);
        Assert.Equal("maintenance", Single(supervisor).Phase);
    }

    [Fact]
    public async Task Park_refuses_an_instance_with_nothing_running_to_park()
    {
        var events = new RecordingJournal();
        var spec = SpecFor("down");
        var supervisor = NewSupervisor(events, spec, out _);
        Adopt(supervisor, spec.Name, consecutiveFailures: 0); // tabled, but its cgroup was never live

        var result = await supervisor.BeginMaintenanceAsync(spec.Name, "scheduler");

        // The release respawns whatever was parked, so parking something that is down would turn
        // somebody else's stop into a start.
        Assert.False(result.Ok);
        Assert.Contains("nothing to park", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Releasing_an_instance_that_is_not_parked_is_a_no_op_success()
    {
        var events = new RecordingJournal();
        var spec = SpecFor("never-parked");
        var supervisor = NewSupervisor(events, spec, out _);

        var result = await supervisor.EndMaintenanceAsync(spec.Name, "scheduler");

        // A leaf calls this unconditionally whatever the work it parked for did.
        Assert.True(result.Ok);
        Assert.Equal("not parked", result.Message);
        Assert.Empty(events.Snapshot());
    }

    [Fact]
    public async Task Releasing_a_park_leaves_the_failure_streak_alone()
    {
        var events = new RecordingJournal();
        var spec = SpecFor("streak-kept");
        var supervisor = NewSupervisor(events, spec, out string cgroup);
        Adopt(supervisor, spec.Name, consecutiveFailures: 3);
        LiveCgroup(cgroup, spec.Name);

        await supervisor.BeginMaintenanceAsync(spec.Name, "scheduler");
        await supervisor.EndMaintenanceAsync(spec.Name, "scheduler");

        // A start would have cleared this; coming out of a park with a crash history the instance never
        // earned is what the release exists to avoid.
        var state = Single(supervisor);
        Assert.Equal(3, state.Restarts);
        Assert.NotEqual("maintenance", state.Phase); // released whatever the spawn did
    }

    [Fact]
    public async Task A_park_nothing_releases_is_released_by_the_daemon()
    {
        var events = new RecordingJournal();
        var spec = SpecFor("forgotten");
        // A deadline of zero makes any park immediately overdue — the same arithmetic the configured
        // limit runs on, without the test waiting it out.
        var supervisor = NewSupervisor(events, spec, out string cgroup, maintenanceMaxMinutes: 0);
        Adopt(supervisor, spec.Name, consecutiveFailures: 0);
        LiveCgroup(cgroup, spec.Name);

        await supervisor.BeginMaintenanceAsync(spec.Name, "scheduler");
        Assert.Equal("maintenance", Single(supervisor).Phase);

        supervisor.Reconcile();

        Assert.NotEqual("maintenance", Single(supervisor).Phase);
    }

    [Fact]
    public void A_park_that_predates_the_daemons_start_is_released()
    {
        var events = new RecordingJournal();
        var spec = SpecFor("survivor");
        var supervisor = NewSupervisor(events, spec, out _, persisted: new InstanceRestartState
        {
            ConsecutiveFailures = 4,
            Phase = nameof(SupervisionPhase.Maintenance),
            MaintenanceSince = DateTime.UtcNow - TimeSpan.FromMinutes(1),
            LastReason = "parked for maintenance (scheduler)",
        });

        // A parked instance holds no process, so the boot restore finds nothing live to adopt and only
        // the persisted counters remember it at all.
        supervisor.RehydrateCounters();

        var state = Single(supervisor);
        Assert.NotEqual("maintenance", state.Phase);
        Assert.Equal("running", state.Desired);
        Assert.Equal(4, state.Restarts); // the streak comes back with it
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static InstanceState Single(InstanceSupervisor supervisor) => Assert.Single(supervisor.List());

    /// <summary>
    /// Give an instance a cgroup that reads as live, and end it a moment later. The daemon's whole
    /// notion of liveness is <c>cgroup.events</c>, so writing <c>populated 0</c> is the process
    /// exiting — which is what lets a real drain run to completion against no real process.
    /// </summary>
    private static void LiveCgroup(string cgroupBase, string name)
    {
        string dir = Path.Combine(cgroupBase, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cgroup.procs"), "");
        File.WriteAllText(Path.Combine(dir, "cgroup.kill"), "");
        string liveness = Path.Combine(dir, "cgroup.events");
        File.WriteAllText(liveness, "populated 1\nfrozen 0\n");

        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            try { File.WriteAllText(liveness, "populated 0\nfrozen 0\n"); }
            catch (Exception) { /* the teardown removed it first — already drained */ }
        });
    }

    /// <summary>
    /// Put one instance into the table in the <c>Running</c> phase with a seeded streak, through the
    /// hot-swap handoff adoption path — the one public door into the state table that forks no game.
    /// The inherited fd is invalid and the spec's FIFO does not exist, so it lands on cgroup-only
    /// supervision, which is the shape an adopted instance has anyway.
    /// </summary>
    private static void Adopt(InstanceSupervisor supervisor, string name, int consecutiveFailures)
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
                    ConsecutiveFailures = consecutiveFailures,
                    GaveUp = false,
                    Phase = nameof(SupervisionPhase.Running),
                    SpawnedAt = DateTime.UtcNow - TimeSpan.FromMinutes(5), // past the grace window
                    DesiredRunning = true,
                },
            },
        };
        string json = JsonSerializer.Serialize(handoff, WatchdogJsonContext.Default.HotSwapHandoff);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        string? prior = Environment.GetEnvironmentVariable(HotSwapHandoff.EnvVarName);
        Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, b64);
        try { supervisor.AdoptFromHandoff(); }
        finally { Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, prior); }
    }

    private static Instance SpecFor(string name) => new()
    {
        Name = name,
        Runtime = InstanceRuntime.Native,
        SocketFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-nofifo-{Guid.NewGuid():N}.fifo"),
        StopCommandTimeoutSeconds = 1,
    };

    private static InstanceSupervisor NewSupervisor(
        RecordingJournal events,
        Instance spec,
        out string cgroupBase,
        int maintenanceMaxMinutes = 60,
        InstanceRestartState? persisted = null)
    {
        var options = new WatchdogOptions
        {
            CgroupMountPoint = Path.Combine(Path.GetTempPath(), $"kgsm-wd-cg-{Guid.NewGuid():N}"),
            StateFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-park-{Guid.NewGuid():N}", "desired-state.json"),
            MaintenanceMaxMinutes = maintenanceMaxMinutes,
        };
        cgroupBase = options.CgroupBasePath;

        var supervision = TestState.Supervision(options);
        if (persisted is not null)
        {
            var snapshot = new PersistedSupervisionState();
            snapshot.Instances[spec.Name] = persisted;
            supervision.Save(snapshot);
        }

        var cgroups = new CgroupManager(options, NullLogger<CgroupManager>.Instance);
        var spawn = new SpawnEngine(cgroups, NullLogger<SpawnEngine>.Instance);
        var state = new SupervisorState { Ready = true, Detail = "test" };
        return new InstanceSupervisor(
            new PerInstanceCrashPolicyTests.SingleInstance(spec),
            spawn,
            cgroups,
            new BackoffPolicy(),
            state,
            TestState.Desired(options),
            supervision,
            TestState.RunHistory(options),
            events,
            events.Lifecycle,
            new UpnpService(NullLogger<UpnpService>.Instance),
            new FirewallPortsService(
                new FirewallPortsServiceTests.FakeFirewall(), NullLogger<FirewallPortsService>.Instance),
            TestState.Sessions(),
            TestMemoryGate.Disabled(),
            options,
            NullLogger<InstanceSupervisor>.Instance);
    }
}
