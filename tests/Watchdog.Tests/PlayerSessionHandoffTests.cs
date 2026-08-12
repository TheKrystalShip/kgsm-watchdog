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
/// The player session map's survival across a hot-swap (<c>HotSwapHandoff.PlayerSessions</c>) — the
/// counterpart to <see cref="PlayerSessionTeardownTests"/>, which covers when the map must be dropped.
/// <para>
/// It is carried for the same reason the FIFO fds are: it cannot be re-derived. The successor's log tail
/// primes at EOF, so the join lines that built the map are behind it forever, and most games' leave lines
/// carry only a bare correlation token — an address or an opaque id, never a display name. A leave landing
/// on an empty map therefore resolves to nothing and is skipped rather than guessed, and that player stays
/// reported as connected until the instance next stops.
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // these mutate the handoff env var — serialize with other env mutators
public sealed class PlayerSessionHandoffTests
{
    [Fact]
    public void The_handoff_blob_round_trips_player_sessions()
    {
        // The daemon is reflection-free: a type the source generator did not emit metadata for throws
        // NotSupportedException at RUNTIME, with no build warning — and the only place this blob is ever
        // serialized for real is the microsecond before an execv, where a throw takes the swap with it.
        var handoff = new HotSwapHandoff
        {
            PlayerSessions =
            {
                ["romestead"] = [new PlayerSession("92.31.7.177:50001", null, "Juno", "92.31.7.177:50001")],
                ["valheim-1"] = [new PlayerSession("651023867", null, "Test", null)],
            },
        };

        string json = JsonSerializer.Serialize(handoff, WatchdogJsonContext.Default.HotSwapHandoff);
        HotSwapHandoff? back = JsonSerializer.Deserialize(json, WatchdogJsonContext.Default.HotSwapHandoff);

        Assert.NotNull(back);
        Assert.Equal(2, back.PlayerSessions.Count);
        PlayerSession juno = Assert.Single(back.PlayerSessions["romestead"]);
        Assert.Equal("92.31.7.177:50001", juno.SessionKey);
        Assert.Equal("Juno", juno.Name);
        Assert.Equal("92.31.7.177:50001", juno.Addr);
        Assert.Null(juno.Id);
    }

    [Fact]
    public void Adopting_a_handoff_restores_the_session_map()
    {
        var sessions = new PlayerSessionStore();
        var supervisor = NewSupervisor(sessions);

        WithHandoff(
            Handoff(("romestead", new PlayerSession("92.31.7.177:50001", null, "Juno", "92.31.7.177:50001"))),
            () => supervisor.AdoptFromHandoff());

        PlayerSessionMap.Session restored = Assert.Single(sessions.GetSessions("romestead"));
        Assert.Equal("Juno", restored.Name);
        Assert.Equal("92.31.7.177:50001", restored.Addr);
    }

    [Fact]
    public void A_leave_after_the_swap_still_resolves_to_the_name_the_join_captured()
    {
        // The payoff, and the whole reason the map exists. Romestead's leave line carries the peer address
        // and nothing else; the name came from a join line the successor will never read. Resolving it is
        // the difference between an honest "Juno left" and a presence event dropped on the floor.
        var sessions = new PlayerSessionStore();
        var supervisor = NewSupervisor(sessions);

        WithHandoff(
            Handoff(("romestead", new PlayerSession("92.31.7.177:50001", null, "Juno", "92.31.7.177:50001"))),
            () => supervisor.AdoptFromHandoff());

        PlayerSessionMap.Session? resolved = sessions.Leave(
            "romestead", sessionKey: "92.31.7.177:50001", id: null, name: null, addr: "92.31.7.177:50001");

        Assert.NotNull(resolved);
        Assert.Equal("Juno", resolved.Value.Name);
        Assert.Empty(sessions.GetSessions("romestead")); // resolved and evicted, as a leave should
    }

    [Fact]
    public void Sessions_are_restored_even_when_no_instance_was_handed_off()
    {
        // The handoff's instance entries cover only instances carrying a live FIFO fd; the session maps
        // cover every instance the ingester tracks. An adopted, cgroup-only instance appears in the second
        // and not the first — and has already lost its console, so it can least afford a second loss.
        var sessions = new PlayerSessionStore();
        var supervisor = NewSupervisor(sessions);

        var handoff = Handoff(("adopted-only", new PlayerSession("player-a", null, "player-a", null)));
        Assert.Empty(handoff.Instances);

        WithHandoff(handoff, () => supervisor.AdoptFromHandoff());

        Assert.Single(sessions.GetSessions("adopted-only"));
    }

    [Fact]
    public void A_session_with_no_key_is_dropped_rather_than_restored()
    {
        // The key is what a later leave resolves against. An entry without one can never be matched, so
        // keeping it would only inflate what GET /players reports — a player nothing can ever retire.
        var sessions = new PlayerSessionStore();
        var supervisor = NewSupervisor(sessions);

        WithHandoff(Handoff(("romestead", new PlayerSession(null, null, "Ghost", null))),
            () => supervisor.AdoptFromHandoff());

        Assert.Empty(sessions.GetSessions("romestead"));
    }

    [Fact]
    public void A_malformed_blob_leaves_the_session_map_empty_rather_than_throwing()
    {
        // Same degrade-don't-wedge rule the instance half already follows: a bad handoff costs presence
        // correlation, never the daemon's boot.
        var sessions = new PlayerSessionStore();
        var supervisor = NewSupervisor(sessions);

        string? prior = Environment.GetEnvironmentVariable(HotSwapHandoff.EnvVarName);
        Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, "!!! not base64 !!!");
        try { supervisor.AdoptFromHandoff(); }
        finally { Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, prior); }

        Assert.Empty(sessions.GetAllSessions());
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static HotSwapHandoff Handoff(params (string Instance, PlayerSession Session)[] sessions)
    {
        var handoff = new HotSwapHandoff();
        foreach (var group in sessions.GroupBy(s => s.Instance, StringComparer.Ordinal))
            handoff.PlayerSessions[group.Key] = [.. group.Select(s => s.Session)];
        return handoff;
    }

    /// <summary>Stage a handoff in the env var for one call, then restore whatever was there.</summary>
    private static void WithHandoff(HotSwapHandoff handoff, Action adopt)
    {
        string json = JsonSerializer.Serialize(handoff, WatchdogJsonContext.Default.HotSwapHandoff);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        string? prior = Environment.GetEnvironmentVariable(HotSwapHandoff.EnvVarName);
        Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, b64);
        try { adopt(); } // clears the env var itself on the way out
        finally { Environment.SetEnvironmentVariable(HotSwapHandoff.EnvVarName, prior); }
    }

    private static InstanceSupervisor NewSupervisor(PlayerSessionStore sessions)
    {
        var options = new WatchdogOptions
        {
            CgroupMountPoint = Path.Combine(Path.GetTempPath(), $"kgsm-wd-cg-{Guid.NewGuid():N}"),
            StateFile = Path.Combine(Path.GetTempPath(), $"kgsm-wd-handoff-{Guid.NewGuid():N}", "desired-state.json"),
        };
        var cgroups = new CgroupManager(options, NullLogger<CgroupManager>.Instance);
        var spawn = new SpawnEngine(cgroups, NullLogger<SpawnEngine>.Instance);
        var state = new SupervisorState { Ready = true, Detail = "test" };
        return new InstanceSupervisor(
            new PerInstanceCrashPolicyTests.SingleInstance(new Instance { Name = "unused" }),
            spawn,
            cgroups,
            new BackoffPolicy(),
            state,
            TestState.Desired(options),
            TestState.Supervision(options),
            TestState.RunHistory(options),
            new RecordingJournal(),
            new UpnpService(NullLogger<UpnpService>.Instance),
            new FirewallPortsService(
                new FirewallPortsServiceTests.FakeFirewall(), NullLogger<FirewallPortsService>.Instance),
            sessions,
            NullLogger<InstanceSupervisor>.Instance);
    }
}
