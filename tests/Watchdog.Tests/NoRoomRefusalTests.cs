using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Control;
using TheKrystalShip.KGSM.Watchdog.Firewall;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// How a capacity refusal leaves the daemon: as a refusal a caller can tell apart, not as a generic
/// failure. A start the gate turns away is nothing being wrong with the instance and nothing having
/// been attempted, so a caller that reads it as a failure retries into the identical answer and reports
/// a fault the server does not have.
/// <para>
/// The distinction travels two ways at once, because the two callers read different things: the
/// <see cref="ActionResult.Refusal"/> field for anything reading JSON, and the HTTP status line for the
/// kgsm CLI, whose transport keeps the status code and discards the body. Neither is a sentence to be
/// matched — <see cref="ActionResult.Message"/> is prose for a person.
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // shares the adoption env-var door with the other supervisor tests
public sealed class NoRoomRefusalTests
{
    [Fact]
    public async Task A_start_the_node_has_no_room_for_is_reported_as_a_refusal()
    {
        // 2000MB posed against a 4096MB instance and a 1024MB floor: it does not fit, and the spawn is
        // never attempted — so the spec being unspawnable is not what produces this answer.
        var supervisor = NewSupervisor(SpecFor("wd-noroom", memoryCapMb: 4096),
            TestMemoryGate.Posed(availableMb: 2000));

        var result = await supervisor.StartAsync("wd-noroom");

        Assert.False(result.Ok);
        Assert.Equal(ActionRefusal.NoRoom, result.Refusal);
        // The sentence still names the figures — the field is what a caller branches on, not what it reads.
        Assert.Contains("4096MB", result.Message);
        Assert.Contains("1024MB", result.Message);
    }

    [Fact]
    public async Task A_start_that_fails_for_any_other_reason_carries_no_refusal()
    {
        // The node has room; the spec has no executable, so the spawn itself throws. That is a failure,
        // and it must NOT come out as a refusal — it is worth retrying once the cause is fixed.
        var supervisor = NewSupervisor(SpecFor("wd-broken", memoryCapMb: 4096),
            TestMemoryGate.Posed(availableMb: 64_000));

        var result = await supervisor.StartAsync("wd-broken");

        Assert.False(result.Ok);
        Assert.Null(result.Refusal);
    }

    [Fact]
    public async Task A_restart_refused_for_room_carries_the_refusal_out_of_the_start_half()
    {
        // The stop half succeeds and the start half is refused, which leaves the instance DOWN. A caller
        // that read this as a generic failure would retry a start that is refused identically for as
        // long as the node is full.
        var supervisor = NewSupervisor(SpecFor("wd-noroom-restart", memoryCapMb: 4096),
            TestMemoryGate.Posed(availableMb: 2000));

        var result = await supervisor.RestartAsync("wd-noroom-restart");

        Assert.False(result.Ok);
        Assert.Equal(ActionRefusal.NoRoom, result.Refusal);
        Assert.Contains("stop ok", result.Message);
    }

    [Fact]
    public async Task An_explicit_start_can_be_forced_past_the_refusal()
    {
        // The same node and the same instance that produced the refusal above. With force the gate is
        // no longer the answer: the spawn is attempted (and fails here, on a spec with no executable —
        // there is no game to start in a unit test), which is the observable difference between "the
        // gate refused this" and "the gate let it through".
        var supervisor = NewSupervisor(SpecFor("wd-noroom-forced", memoryCapMb: 4096),
            TestMemoryGate.Posed(availableMb: 2000));

        var result = await supervisor.StartAsync("wd-noroom-forced", CancellationToken.None, force: true);

        Assert.False(result.Ok);
        Assert.Null(result.Refusal);          // not a capacity refusal — it got past the gate
        Assert.Contains("spawn failed", result.Message);
    }

    [Fact]
    public async Task A_restart_never_forces_on_its_own()
    {
        // RestartAsync is the scheduler's verb over the control socket, and nobody is at a terminal for
        // it. Its start half takes the verdict as final, exactly like the autostart and the
        // crash-restart do.
        var supervisor = NewSupervisor(SpecFor("wd-noroom-restart-force", memoryCapMb: 4096),
            TestMemoryGate.Posed(availableMb: 2000));

        var result = await supervisor.RestartAsync("wd-noroom-restart-force");

        Assert.Equal(ActionRefusal.NoRoom, result.Refusal);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("maybe", false)]
    public void The_force_query_flag_reads_unset_unless_it_plainly_says_otherwise(string? flag, bool expected)
    {
        // A spelling nothing recognises is a start WITHOUT the override, never a 400: the protection is
        // what a caller gets by not asking for it, and that has to include asking badly.
        Assert.Equal(expected, ControlEndpoints.IsTrue(flag));
    }

    [Theory]
    [InlineData(true, null, 200)]
    [InlineData(false, ActionRefusal.NoRoom, 507)]
    [InlineData(false, null, 409)]
    public void The_status_line_separates_acted_from_refused_from_failed(bool ok, string? refusal, int expected)
    {
        // 507 Insufficient Storage, because the request is well-formed and the instance is fine — the
        // host cannot hold what it asks for right now. A bash caller reads this and nothing else.
        Assert.Equal(expected, ControlEndpoints.StatusFor(new ActionResult("x", ok, "why", refusal)));
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static Instance SpecFor(string name, int memoryCapMb) => new()
    {
        Name = name,
        Runtime = InstanceRuntime.Native,
        SocketFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-nofifo-{Guid.NewGuid():N}.fifo"),
        MemoryCapMb = memoryCapMb,
    };

    private static InstanceSupervisor NewSupervisor(Instance spec, MemoryGate gate)
    {
        var options = new WatchdogOptions
        {
            // Empty temp cgroup base → no instance cgroup is ever populated.
            CgroupMountPoint = Path.Combine(Path.GetTempPath(), $"kgsm-wd-cg-{Guid.NewGuid():N}"),
            StateFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-noroom-{Guid.NewGuid():N}", "desired-state.json"),
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
