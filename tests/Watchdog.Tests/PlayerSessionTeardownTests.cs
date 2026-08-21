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
/// Covers <c>InstanceSupervisor.ForgetPlayerSessions</c> — the rule that a
/// <see cref="PlayerSessionStore"/> map never outlives the process it describes.
/// <para>
/// The map answers the control surface's <c>GET /players</c>, which kgsm-api reconciles its permanent
/// roster from on every startup. A map left standing over a dead process therefore does not merely go
/// stale: it is copied into a durable record as players who are connected to a server that is not
/// running. The ingesters clear it when the log rolls to a fresh session — the NEXT start — so the
/// teardown edges asserted here are what make the down-window honest.
/// </para>
/// <para>
/// The same injection door as <see cref="PerInstanceCrashPolicyTests"/>: an instance adopted in the
/// <c>Running</c> phase over an empty temp cgroup base, so nothing is ever populated and a single
/// <c>Reconcile()</c> takes the "it exited" branch with no real process involved.
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // adoption reads/writes the handoff env var — serialize with other env mutators
public sealed class PlayerSessionTeardownTests
{
    [Fact]
    public async Task Stop_clears_the_session_map()
    {
        var sessions = TestState.Sessions();
        var spec = SpecFor("wd-sessions-stop");
        var supervisor = NewSupervisor(spec, sessions);

        AdoptRunning(supervisor, spec.Name);
        sessions.Join(spec.Name, "76561198144397568", "76561198144397568", null, null);
        Assert.Single(sessions.GetSessions(spec.Name));

        await supervisor.StopAsync(spec.Name);

        Assert.Empty(sessions.GetSessions(spec.Name));
    }

    [Fact]
    public async Task Stop_clears_the_session_map_of_an_instance_the_table_does_not_hold()
    {
        // The path this bug arrived on: an instance stopped while the daemon was not tracking it (the
        // record dropped by an earlier teardown, or a stop re-issued against an already-stopped
        // instance) still answers "not running" — and must still leave nobody behind, because the
        // ingester that populated the map is not tracking table entries at all.
        var sessions = TestState.Sessions();
        var spec = SpecFor("wd-sessions-untracked");
        var supervisor = NewSupervisor(spec, sessions);

        sessions.Join(spec.Name, "76561198272660800", "76561198272660800", null, null);

        var result = await supervisor.StopAsync(spec.Name);

        Assert.True(result.Ok);
        Assert.Equal("not running", result.Message);
        Assert.Empty(sessions.GetSessions(spec.Name));
    }

    [Fact]
    public void A_crash_clears_the_session_map()
    {
        // Crash-restart is on (the default), so this exit is classified as a crash and a respawn is
        // pending — the sessions still ended when the process did.
        var sessions = TestState.Sessions();
        var spec = SpecFor("wd-sessions-crash");
        var supervisor = NewSupervisor(spec, sessions);

        AdoptRunning(supervisor, spec.Name);
        sessions.Join(spec.Name, "player-a", null, "player-a", null);

        supervisor.Reconcile();

        Assert.Equal("restart-pending", Assert.Single(supervisor.List()).Phase);
        Assert.Empty(sessions.GetSessions(spec.Name));
    }

    [Fact]
    public void Clearing_the_map_emits_no_player_events()
    {
        // Dropping the map is bookkeeping, not an observation. A leave per tracked session would be a
        // disconnect record no game ever reported — and the crash event this teardown DOES emit is
        // already what tells a consumer to reset the whole roster.
        var events = new RecordingJournal();
        var sessions = TestState.Sessions();
        var spec = SpecFor("wd-sessions-silent");
        var supervisor = NewSupervisor(spec, sessions, events);

        AdoptRunning(supervisor, spec.Name);
        sessions.Join(spec.Name, "player-c", null, "player-c", null);

        supervisor.Reconcile();

        Assert.True(events.WaitFor("instance-crashed"));
        Assert.DoesNotContain("instance-player-left", events.Snapshot());
        Assert.Empty(sessions.GetSessions(spec.Name));
    }

    [Fact]
    public void Giving_up_on_an_instance_clears_the_session_map()
    {
        // The terminal branch, and the one no other reset covers: retries exhausted → phase failed, no
        // respawn, so no fresh log session will ever come along to clear the map for us.
        var sessions = TestState.Sessions();
        var spec = SpecFor("wd-sessions-failed", crashMaxRestarts: 1);
        var supervisor = NewSupervisor(spec, sessions);

        AdoptRunning(supervisor, spec.Name, consecutiveFailures: 1); // one restart already spent
        sessions.Join(spec.Name, "player-b", null, "player-b", null);

        supervisor.Reconcile();

        Assert.Equal("failed", Assert.Single(supervisor.List()).Phase);
        Assert.Empty(sessions.GetSessions(spec.Name));
    }

    [Fact]
    public async Task Deregistering_an_instance_drops_its_map_entirely()
    {
        // An uninstalled instance is not "an instance with nobody on it" — it is not there. Clearing
        // alone would leave it answering GET /players with an empty list forever.
        var sessions = TestState.Sessions();
        var spec = SpecFor("wd-sessions-forget");
        var supervisor = NewSupervisor(spec, sessions);

        AdoptRunning(supervisor, spec.Name);
        sessions.Join(spec.Name, "player-d", null, "player-d", null);

        await supervisor.ForgetAsync(spec.Name);

        Assert.DoesNotContain(spec.Name, sessions.GetAllSessions().Keys);
    }

    [Fact]
    public void Clearing_one_instance_leaves_every_other_instance_alone()
    {
        var sessions = TestState.Sessions();
        var spec = SpecFor("wd-sessions-scoped");
        var supervisor = NewSupervisor(spec, sessions);

        AdoptRunning(supervisor, spec.Name);
        sessions.Join(spec.Name, "gone", null, "gone", null);
        sessions.Join("wd-other-instance", "still-here", null, "still-here", null);

        supervisor.Reconcile();

        Assert.Empty(sessions.GetSessions(spec.Name));
        Assert.Single(sessions.GetSessions("wd-other-instance"));
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static void AdoptRunning(InstanceSupervisor supervisor, string name, int consecutiveFailures = 0)
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

    private static Instance SpecFor(string name, int? crashMaxRestarts = null) => new()
    {
        Name = name,
        SocketFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-nofifo-{Guid.NewGuid():N}.fifo"), // never exists → cgroup-only adopt
        CrashMaxRestarts = crashMaxRestarts,
    };

    private static InstanceSupervisor NewSupervisor(
        Instance spec, PlayerSessionStore sessions, RecordingJournal? events = null)
    {
        var options = new WatchdogOptions
        {
            // Empty temp cgroup base → no instance cgroup ever populated → the reconcile pass sees "exited".
            CgroupMountPoint = Path.Combine(Path.GetTempPath(), $"kgsm-wd-cg-{Guid.NewGuid():N}"),
            StateFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-sessions-{Guid.NewGuid():N}", "desired-state.json"),
        };
        var cgroups = new CgroupManager(options, NullLogger<CgroupManager>.Instance);
        var spawn = new SpawnEngine(cgroups, NullLogger<SpawnEngine>.Instance);
        var state = new SupervisorState { Ready = true, Detail = "test" };
        RecordingJournal recorder = events ?? new RecordingJournal();
        return new InstanceSupervisor(
            new PerInstanceCrashPolicyTests.SingleInstance(spec),
            spawn,
            cgroups,
            new BackoffPolicy(),
            state,
            TestState.Desired(options),
            TestState.Supervision(options),
            TestState.RunHistory(options),
            recorder,
            recorder.Lifecycle,
            new UpnpService(NullLogger<UpnpService>.Instance),
            new FirewallPortsService(
                new FirewallPortsServiceTests.FakeFirewall(), NullLogger<FirewallPortsService>.Instance),
            sessions,
            TestMemoryGate.Disabled(),
            NullLogger<InstanceSupervisor>.Instance);
    }

}
