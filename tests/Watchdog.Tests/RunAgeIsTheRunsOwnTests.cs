using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Firewall;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// An instance's reported start is the RUN's, not the daemon's.
/// <para>
/// A game outlives the daemon supervising it — <c>KillMode=process</c> is what makes that true, and a
/// hot-swap or a cold bounce leaves it running to be re-adopted. Supervision starts over at each of
/// those, and that clock has to start over: it is what the post-spawn grace window and the stability
/// reset are measured from. The run's age is a different quantity that must not start over with it, or
/// every deploy re-dates the whole fleet to the deploy.
/// </para>
/// </summary>
public sealed class RunAgeIsTheRunsOwnTests : IDisposable
{
    private readonly string _cgroupRoot;
    private readonly string _stateRoot;

    public RunAgeIsTheRunsOwnTests()
    {
        string id = Guid.NewGuid().ToString("N");
        _cgroupRoot = Path.Combine(Path.GetTempPath(), "kgsm-wd-age-cg-" + id);
        _stateRoot = Path.Combine(Path.GetTempPath(), "kgsm-wd-age-state-" + id);
        Directory.CreateDirectory(_cgroupRoot);
        Directory.CreateDirectory(_stateRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cgroupRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_stateRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void An_adopted_run_reports_its_own_age_not_the_moment_the_daemon_took_it_over()
    {
        var ranSince = new DateTime(2026, 8, 21, 23, 15, 32, DateTimeKind.Utc);
        var events = new RecordingJournal();
        var supervisor = NewSupervisor(events, SpecFor("ketchup"));
        SetPopulated("ketchup");

        AdoptFromHandoff(supervisor, "ketchup", runStartedAt: ranSince);

        InstanceState? state = supervisor.Status("ketchup");
        Assert.NotNull(state);
        Assert.Equal(ranSince, state!.SpawnedAt);
    }

    [Fact]
    public void An_adopted_run_whose_leader_cannot_be_read_reports_an_unknown_age_rather_than_now()
    {
        // The handoff carries no measurement and the fake cgroup holds no readable leader, so there is
        // nothing to date the run by. Null is the honest answer; stamping the adoption would read as a
        // server that just started.
        var events = new RecordingJournal();
        var supervisor = NewSupervisor(events, SpecFor("ketchup"));
        SetPopulated("ketchup");

        AdoptFromHandoff(supervisor, "ketchup", runStartedAt: null);

        InstanceState? state = supervisor.Status("ketchup");
        Assert.NotNull(state);
        Assert.Null(state!.SpawnedAt);
    }

    /// <summary>
    /// Put one instance in the table in the <c>Running</c> phase through the hot-swap handoff. The
    /// inherited fd is invalid and the spec's FIFO does not exist, so adoption lands on cgroup-only
    /// supervision — which is all this needs, and needs no real process.
    /// </summary>
    private static void AdoptFromHandoff(InstanceSupervisor supervisor, string name, DateTime? runStartedAt)
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
                    Phase = nameof(SupervisionPhase.Running),
                    SpawnedAt = DateTime.UtcNow,
                    RunStartedAt = runStartedAt,
                    DesiredRunning = true,
                },
            },
        };
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(handoff, WatchdogJsonContext.Default.HotSwapHandoff)));

        string? prior = Environment.GetEnvironmentVariable(HotSwapHandoff.EnvVarName);
        Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, b64);
        try { supervisor.AdoptFromHandoff(); }
        finally { Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, prior); }
    }

    private void SetPopulated(string instance)
    {
        string dir = Path.Combine(_cgroupRoot, "kgsm.slice", instance);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cgroup.events"), "populated 1\nfrozen 0\n");
    }

    private static Instance SpecFor(string name) => new()
    {
        Name = name,
        SocketFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-nofifo-{Guid.NewGuid():N}.fifo"),
    };

    private InstanceSupervisor NewSupervisor(RecordingJournal events, Instance spec)
    {
        var options = new WatchdogOptions
        {
            CgroupMountPoint = _cgroupRoot,
            CgroupBaseName = "kgsm.slice",
            StateFile = Path.Combine(_stateRoot, "desired-state.json"),
        };
        var cgroups = new CgroupManager(options, NullLogger<CgroupManager>.Instance);
        return new InstanceSupervisor(
            new PerInstanceCrashPolicyTests.SingleInstance(spec),
            new SpawnEngine(cgroups, NullLogger<SpawnEngine>.Instance),
            cgroups,
            new BackoffPolicy(),
            new SupervisorState { Ready = true, Detail = "test" },
            TestState.Desired(options),
            TestState.Supervision(options),
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
