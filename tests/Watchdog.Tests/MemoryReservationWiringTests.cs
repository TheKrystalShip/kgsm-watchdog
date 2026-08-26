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
/// Where the supervisor discharges a memory reservation. Readiness is the signal that normally does it
/// (<see cref="NativePlayerPresenceIngesterTests"/> covers that half); these are the paths on which the
/// instance will <em>never</em> report ready, and on which a reservation left standing would subtract
/// memory from every later start for as long as the backstop takes to notice.
/// <para>
/// The gate is posed over a node of a known size — the assertions are about which call sites move the
/// ledger, and proving a memory bound by consuming memory would be self-defeating. No game is started:
/// the spawn either fails on validation before any side effect, or the instance is injected through the
/// hot-swap handoff adoption path (the one public door into the state table that does not fork).
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // adoption reads/writes the handoff env var — serialize with other env mutators
public sealed class MemoryReservationWiringTests
{
    [Fact]
    public async Task A_spawn_that_fails_releases_the_reservation_it_took()
    {
        // The spec carries no executable, so SpawnEngine rejects it before any side effect: the gate
        // allowed the start and reserved for it, and nothing is ever going to claim that memory.
        var gate = TestMemoryGate.Posed(availableMb: 10_000);
        var spec = SpecFor("wd-res-spawnfail", memoryCapMb: 4096);
        var supervisor = NewSupervisor(spec, gate);

        var result = await supervisor.StartAsync(spec.Name);

        Assert.False(result.Ok);
        Assert.Equal(0, gate.OutstandingReservedMb());
    }

    [Fact]
    public async Task A_stop_releases_the_reservation_of_the_start_it_interrupted()
    {
        // A stop that lands while the instance is still starting: it will never reach readiness now, so
        // the reservation goes with it rather than waiting out the backstop.
        var gate = TestMemoryGate.Posed(availableMb: 10_000);
        var spec = SpecFor("wd-res-stop", memoryCapMb: 4096);
        var supervisor = NewSupervisor(spec, gate);
        AdoptRunning(supervisor, spec.Name);
        gate.TryReserve(spec.Name, spec); // as TrySpawn did for the start this stop interrupts
        Assert.Equal(4096, gate.OutstandingReservedMb());

        await supervisor.StopAsync(spec.Name);

        Assert.Equal(0, gate.OutstandingReservedMb());
    }

    [Fact]
    public void A_run_that_ends_releases_the_reservation_on_the_same_edge_the_sessions_are_cleared()
    {
        // The cgroup emptied while the instance was still desired-running — a crash during boot, before
        // any ready line. Whatever the restart policy decides next, THIS run is over and its reservation
        // with it; a restart takes a fresh one through TrySpawn.
        var gate = TestMemoryGate.Posed(availableMb: 10_000);
        var spec = SpecFor("wd-res-crash", memoryCapMb: 4096, crashRestart: false);
        var supervisor = NewSupervisor(spec, gate);
        AdoptRunning(supervisor, spec.Name);
        gate.TryReserve(spec.Name, spec);

        supervisor.Reconcile(); // the cgroup base is empty, so this pass sees "it exited"

        Assert.Equal(0, gate.OutstandingReservedMb());
    }

    [Fact]
    public async Task Deregistering_releases_the_reservation()
    {
        // An instance being uninstalled cannot report ready either, and its name may never be seen
        // again — a reservation left here would hold memory back for something that no longer exists.
        var gate = TestMemoryGate.Posed(availableMb: 10_000);
        var spec = SpecFor("wd-res-forget", memoryCapMb: 4096);
        var supervisor = NewSupervisor(spec, gate);
        AdoptRunning(supervisor, spec.Name);
        gate.TryReserve(spec.Name, spec);

        await supervisor.ForgetAsync(spec.Name);

        Assert.Equal(0, gate.OutstandingReservedMb());
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static Instance SpecFor(string name, int memoryCapMb, bool? crashRestart = null) => new()
    {
        Name = name,
        SocketFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-nofifo-{Guid.NewGuid():N}.fifo"), // never exists → cgroup-only adopt
        MemoryCapMb = memoryCapMb,
        CrashRestart = crashRestart,
    };

    /// <summary>Inject one instance into the table in the <c>Running</c> phase, with no process behind it.</summary>
    private static void AdoptRunning(InstanceSupervisor supervisor, string name)
    {
        var handoff = new HotSwapHandoff
        {
            Instances =
            {
                new HotSwapEntry
                {
                    Name = name,
                    FifoFd = -1,   // invalid → no inherited-fd adopt; the FIFO re-open then fails → cgroup-only
                    FifoPath = "",
                    ConsecutiveFailures = 0,
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
        try { supervisor.AdoptFromHandoff(); } // clears the env var itself on the way out
        finally { Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, prior); }
    }

    private static InstanceSupervisor NewSupervisor(Instance spec, MemoryGate gate)
    {
        var options = new WatchdogOptions
        {
            // Empty temp cgroup base → no instance cgroup is ever populated.
            CgroupMountPoint = Path.Combine(Path.GetTempPath(), $"kgsm-wd-cg-{Guid.NewGuid():N}"),
            StateFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-res-{Guid.NewGuid():N}", "desired-state.json"),
        };
        var cgroups = new CgroupManager(options, NullLogger<CgroupManager>.Instance);
        var events = new RecordingJournal();
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
            gate,
            options,
            NullLogger<InstanceSupervisor>.Instance);
    }
}
