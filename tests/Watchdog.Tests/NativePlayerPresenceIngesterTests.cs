using TheKrystalShip.KGSM.Watchdog.Events;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the native ingester end-to-end through one ingest pass (against real temp files + faked
/// kgsm-lib seams): first-attach-at-EOF skips a pre-existing append-only log's history, appended join/left
/// lines emit with the right name/provenance/param order, a non-native instance and a native instance
/// with no patterns are skipped, and the tail cursor resumes across passes (no redelivery).
/// <para>
/// Also covers the player-presence contract §4 correlation + dedup, fed the REAL log lines from all
/// five validated games (matcher + <see cref="PlayerSessionMap"/> + <see cref="EventChannelTail"/>
/// together): stationeers (self-identifying), romestead (addr-correlated, incl. co-NAT distinct ports),
/// Valheim (doubled join lines + a 6x repeated leave burst, key-correlated), Core Keeper (opaque key +
/// leave reason), Minecraft (name-keyed, addr on the join line only, surrounded by lines that must not
/// match), and a log-rotation (inode change) resetting an instance's session map.
/// </para>
/// </summary>
public sealed class NativePlayerPresenceIngesterTests : IDisposable
{
    private const string Joined = @"\[JOIN\] (?<name>\S+) \((?<id>\d+)\)";
    private const string Left = @"\[LEAVE\] (?<name>\S+) \((?<id>\d+)\)";

    private readonly string _root;
    // A separate real-filesystem tree standing in for cgroupfs (mirrors CgroupManagerTests: no mocks,
    // real `cgroup.events` files with the kernel's `populated 0|1` line — the true IsPopulated code path).
    private readonly string _cgroupRoot;

    public NativePlayerPresenceIngesterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kgsm-wd-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _cgroupRoot = Path.Combine(Path.GetTempPath(), "kgsm-wd-native-cg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cgroupRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_cgroupRoot, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Write (or overwrite) a fake instance cgroup's `cgroup.events` so
    /// <see cref="CgroupManager.IsPopulated"/> reads it exactly as it would a real cgroupfs.</summary>
    private void SetPopulated(string instance, bool populated)
    {
        string dir = Path.Combine(_cgroupRoot, "kgsm.slice", instance);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cgroup.events"), $"populated {(populated ? 1 : 0)}\nfrozen 0\n");
    }

    private CgroupManager NewCgroups()
        => new(new WatchdogOptions { CgroupMountPoint = _cgroupRoot, CgroupBaseName = "kgsm.slice" }, NullLogger<CgroupManager>.Instance);

    [Fact]
    public void Discovers_instance_names_from_the_two_level_tree()
    {
        Directory.CreateDirectory(Path.Combine(_root, "factorio", "factorio-test"));
        Directory.CreateDirectory(Path.Combine(_root, "minecraft", "mc-07"));

        var names = NativePlayerPresenceIngester.DiscoverInstanceNames(_root).ToHashSet();

        Assert.Equal(new HashSet<string> { "factorio-test", "mc-07" }, names);
    }

    [Fact]
    public void First_attach_skips_history_then_emits_appended_join_and_left()
    {
        // A pre-existing, append-only log with a stale join already in it — must NOT be replayed.
        string log = MakeInstanceWithLog("factorio", "factorio-test",
            "2026-06-20 11:00:00 [JOIN] Stale (1) joined the game\n");

        var fake = new FakeInstanceService();
        fake.Add(Native("factorio-test", log, Joined, Left));

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);

        // Pass 1: primes at EOF → the stale join is skipped, nothing emitted.
        ingester.IngestOnce(_root);
        Assert.Empty(rec.Calls);

        // A real join arrives after we attached.
        File.AppendAllText(log, "2026-06-20 12:00:00 [JOIN] Alice (76561198000000000) joined the game\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls);
        var join = rec.Calls[0];
        Assert.Equal("instance_player_joined", join.Type);
        Assert.Equal("system:watchdog", join.Actor);
        Assert.Equal("system", join.Origin);
        Assert.Equal("factorio-test", join.String("InstanceName"));
        Assert.Equal("76561198000000000", join.String("PlayerId"));
        Assert.Equal("Alice", join.String("PlayerName"));
        // This pattern captures no address, so the field is a real JSON null — not the empty string the
        // positional-args path had to send for kgsm to map back to null at the far end.
        Assert.True(join.IsNull("PlayerAddr"));
        // sessionKey falls back to id (key ?? addr ?? id ?? name).
        Assert.Equal("76561198000000000", join.String("SessionKey"));
        // A join has no reason, so the field is absent rather than null.
        Assert.False(join.Data.TryGetProperty("Reason", out _));

        // ...then a leave; the tail cursor resumes, so only the new line emits.
        File.AppendAllText(log, "2026-06-20 12:05:00 [LEAVE] Alice (76561198000000000) left the game\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        var left = rec.Calls[1];
        Assert.Equal("instance_player_left", left.Type);
        // Identity resolved via the session map (the join's captures); this pattern has no reason group,
        // so the leave carries the field as an explicit null.
        Assert.Equal("factorio-test", left.String("InstanceName"));
        Assert.Equal("76561198000000000", left.String("PlayerId"));
        Assert.Equal("Alice", left.String("PlayerName"));
        Assert.True(left.IsNull("PlayerAddr"));
        Assert.Equal("76561198000000000", left.String("SessionKey"));
        Assert.True(left.IsNull("Reason"));
    }

    [Fact]
    public void Container_instance_is_skipped()
    {
        string log = MakeInstanceWithLog("vrising", "vr-1", "");
        var fake = new FakeInstanceService();
        fake.Add(new Instance
        {
            Name = "vr-1",
            Runtime = InstanceRuntime.Container, // the container ingester's job, not this one
            LogFile = log,
            PlayerJoinedRegex = Joined,
            PlayerLeftRegex = Left,
        });

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);

        ingester.IngestOnce(_root);
        File.AppendAllText(log, "[JOIN] Nope (1) joined\n");
        ingester.IngestOnce(_root);

        Assert.Empty(rec.Calls);
    }

    [Fact]
    public void Native_instance_with_no_patterns_at_all_is_watched_but_stays_silent_without_a_start_edge()
    {
        // Empty player AND readiness patterns: per the widened enable gate this is no longer a hard
        // "skip" (the immediate-readiness fallback always applies to an empty startup_success_regex —
        // see the next test), but with no cgroup fixture registered the instance never reports
        // populated, so no start edge is ever observed and nothing fires — presence detection stays
        // honestly disabled either way.
        string log = MakeInstanceWithLog("terraria", "tw-1", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("tw-1", log, joined: "", left: "")); // presence disabled (honest unknown)

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);

        ingester.IngestOnce(_root);
        File.AppendAllText(log, "[JOIN] Nope (1) joined\n");
        ingester.IngestOnce(_root);

        Assert.Empty(rec.Calls);
    }

    [Fact]
    public void Native_instance_with_no_player_patterns_and_an_invalid_readiness_pattern_is_truly_skipped()
    {
        // The one case that's genuinely a "nothing to detect" skip: no player patterns AND a NON-EMPTY
        // but invalid readiness regex (a real blueprint bug — never silently substituted with the
        // immediate fallback, which is reserved for a truly EMPTY pattern).
        string log = MakeInstanceWithLog("terraria", "tw-2", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("tw-2", log, joined: "", left: "", ready: "(?<unterminated"));

        var cgroups = NewCgroups();
        SetPopulated("tw-2", populated: true); // even if it WERE running, a skipped instance is never built
        var rec = new RecordingJournal();
        var ingester = NewIngester(rec, fake, cgroups);

        ingester.IngestOnce(_root);
        ingester.IngestOnce(_root);

        Assert.Empty(rec.Calls); // no instance-ready, no presence — genuinely nothing configured
    }

    [Fact]
    public void Both_null_capture_is_dropped_not_emitted()
    {
        // A pattern with no id/name group: a match with neither capture must not reach the wire.
        string log = MakeInstanceWithLog("factorio", "factorio-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("factorio-test", log, joined: "a player connected", left: ""));

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);

        ingester.IngestOnce(_root);
        File.AppendAllText(log, "a player connected\n");
        ingester.IngestOnce(_root);

        Assert.Empty(rec.Calls);
    }

    // ---- contract §4: real-log-line correlation across the four validated games ---------------

    [Fact]
    public void Stationeers_self_identifying_join_and_leave_resolve_the_same_session()
    {
        string log = MakeInstanceWithLog("stationeers", "stationeers-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("stationeers-test", log,
            joined: @"Client (?<name>.+?) \((?<id>\d+)\) is ready",
            left: @"Client disconnected: \d+ \| (?<name>.+?)\s+connectTime:.*ClientId: (?<id>\d+)"));

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);
        ingester.IngestOnce(_root); // primes at EOF

        File.AppendAllText(log, "16:23:51: Client Heisen (76561198144397568) is ready\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls);
        Assert.Equal("instance_player_joined", rec.Calls[0].Type);
        Assert.Equal("stationeers-test", rec.Calls[0].String("InstanceName"));
        Assert.Equal("76561198144397568", rec.Calls[0].String("PlayerId"));
        Assert.Equal("Heisen", rec.Calls[0].String("PlayerName"));
        Assert.True(rec.Calls[0].IsNull("PlayerAddr"));
        Assert.Equal("76561198144397568", rec.Calls[0].String("SessionKey"));

        File.AppendAllText(log,
            "16:24:23: Client disconnected: 684548920970441496 | Heisen      connectTime: 58.9s, ClientId: 76561198144397568\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        Assert.Equal("instance_player_left", rec.Calls[1].Type);
        // Resolved via the map (self-identifying here, so it matches what the leave line itself carries).
        Assert.Equal("stationeers-test", rec.Calls[1].String("InstanceName"));
        Assert.Equal("76561198144397568", rec.Calls[1].String("PlayerId"));
        Assert.Equal("Heisen", rec.Calls[1].String("PlayerName"));
        Assert.True(rec.Calls[1].IsNull("PlayerAddr"));
    }

    [Fact]
    public void Romestead_addr_correlated_leave_resolves_name_and_conat_sessions_stay_distinct()
    {
        string log = MakeInstanceWithLog("romestead", "romestead-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("romestead-test", log,
            joined: @"Character '(?<name>[^']+)' \(Peer \d+ - (?<addr>[\d.]+:\d+)\) logged in",
            left: @"Peer (?<addr>[\d.]+:\d+) disconnected"));

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);
        ingester.IngestOnce(_root);

        // Two players behind the same NAT gateway (co-NAT) — same IP, distinct ports.
        File.AppendAllText(log,
            "Character 'Aelia' (Peer 0 - 86.191.216.57:58845) logged in with external id '', assigned to player id 1\n" +
            "Character 'Brutus' (Peer 1 - 86.191.216.57:53376) logged in with external id '', assigned to player id 2\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        // No id in this pattern — a real JSON null, with the address standing in as the session key.
        Assert.True(rec.Calls[0].IsNull("PlayerId"));
        Assert.Equal("Aelia", rec.Calls[0].String("PlayerName"));
        Assert.Equal("86.191.216.57:58845", rec.Calls[0].String("PlayerAddr"));
        Assert.Equal("86.191.216.57:58845", rec.Calls[0].String("SessionKey"));
        Assert.True(rec.Calls[1].IsNull("PlayerId"));
        Assert.Equal("Brutus", rec.Calls[1].String("PlayerName"));
        Assert.Equal("86.191.216.57:53376", rec.Calls[1].String("PlayerAddr"));

        // Aelia's bare-addr leave must resolve her name — and must not disturb Brutus's session.
        File.AppendAllText(log, "Peer 86.191.216.57:58845 disconnected - RemoteConnectionClose\n");
        ingester.IngestOnce(_root);

        Assert.Equal(3, rec.Calls.Count);
        Assert.Equal("instance_player_left", rec.Calls[2].Type);
        Assert.Equal("romestead-test", rec.Calls[2].String("InstanceName"));
        Assert.True(rec.Calls[2].IsNull("PlayerId"));
        Assert.Equal("Aelia", rec.Calls[2].String("PlayerName"));
        Assert.Equal("86.191.216.57:58845", rec.Calls[2].String("SessionKey"));

        // Brutus's own leave still resolves independently — the co-NAT sessions never collided.
        File.AppendAllText(log, "Peer 86.191.216.57:53376 disconnected - RemoteConnectionClose\n");
        ingester.IngestOnce(_root);

        Assert.Equal(4, rec.Calls.Count);
        Assert.Equal("Brutus", rec.Calls[3].String("PlayerName"));
        Assert.Equal("86.191.216.57:53376", rec.Calls[3].String("SessionKey"));
    }

    [Fact]
    public void Valheim_doubled_join_lines_dedup_and_a_6x_repeated_leave_burst_dedups_key_correlated()
    {
        string log = MakeInstanceWithLog("valheim", "valheim-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("valheim-test", log,
            joined: @"Got character ZDOID from (?<name>.+?) : (?<key>\d+):\d+",
            left: @"Destroying abandoned non persistent zdo \d+:\d+ owner (?<key>\d+)"));

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);
        ingester.IngestOnce(_root);

        // Every real Valheim line appears twice: a Console-wrapped form and a bare form.
        File.AppendAllText(log,
            "07/01/2026 16:56:10: Console: [Info   :   Unity Log] Got character ZDOID from Test : 651023867:1\n" +
            "07/01/2026 16:56:10: Got character ZDOID from Test : 651023867:1\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls); // the doubled line dedups to exactly one join
        Assert.Equal("instance_player_joined", rec.Calls[0].Type);
        Assert.Equal("valheim-test", rec.Calls[0].String("InstanceName"));
        Assert.True(rec.Calls[0].IsNull("PlayerId"));
        Assert.Equal("Test", rec.Calls[0].String("PlayerName"));
        Assert.True(rec.Calls[0].IsNull("PlayerAddr"));
        Assert.Equal("651023867", rec.Calls[0].String("SessionKey"));

        // The cleanup burst re-logs the same disconnect up to 6x.
        for (int i = 0; i < 6; i++)
            File.AppendAllText(log, "07/01/2026 16:56:21: Destroying abandoned non persistent zdo 651023867:1 owner 651023867\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count); // exactly one left; the other 5 deduped via evict
        Assert.Equal("instance_player_left", rec.Calls[1].Type);
        Assert.Equal("valheim-test", rec.Calls[1].String("InstanceName"));
        Assert.True(rec.Calls[1].IsNull("PlayerId"));
        Assert.Equal("Test", rec.Calls[1].String("PlayerName"));
        Assert.True(rec.Calls[1].IsNull("PlayerAddr"));
        Assert.Equal("651023867", rec.Calls[1].String("SessionKey"));
        Assert.True(rec.Calls[1].IsNull("Reason"));
    }

    [Fact]
    public void Corekeeper_opaque_key_join_and_leave_with_reason_resolve_and_evict()
    {
        string log = MakeInstanceWithLog("corekeeper", "corekeeper-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("corekeeper-test", log,
            joined: @"\[userid:(?<key>\d+)\] player (?<name>.+?) connected",
            left: @"Disconnected from userid:(?<key>\d+) with reason (?<reason>\S+)"));

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);
        ingester.IngestOnce(_root);

        File.AppendAllText(log, "[userid:3801603394] player Woltah connected islocalplayer=False\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls);
        Assert.Equal("corekeeper-test", rec.Calls[0].String("InstanceName"));
        Assert.True(rec.Calls[0].IsNull("PlayerId"));
        Assert.Equal("Woltah", rec.Calls[0].String("PlayerName"));
        Assert.True(rec.Calls[0].IsNull("PlayerAddr"));
        Assert.Equal("3801603394", rec.Calls[0].String("SessionKey"));

        File.AppendAllText(log, "Disconnected from userid:3801603394 with reason App_Min\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        Assert.Equal("instance_player_left", rec.Calls[1].Type);
        Assert.Equal("corekeeper-test", rec.Calls[1].String("InstanceName"));
        Assert.True(rec.Calls[1].IsNull("PlayerId"));
        Assert.Equal("Woltah", rec.Calls[1].String("PlayerName"));
        Assert.True(rec.Calls[1].IsNull("PlayerAddr"));
        Assert.Equal("3801603394", rec.Calls[1].String("SessionKey"));
        Assert.Equal("App_Min", rec.Calls[1].String("Reason"));
    }

    [Fact]
    public void Minecraft_name_keyed_join_and_leave_correlate_and_the_surrounding_lines_are_ignored()
    {
        // Minecraft logs four lines around one session and only two of them are the connection pair.
        // The username is the correlation token (`key`), because the address appears on the join line
        // alone and the account UUID is on a separate line a per-line matcher cannot reach.
        string log = MakeInstanceWithLog("minecraft", "minecraft-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("minecraft-test", log,
            joined: @"\[Server thread/INFO\]: (?<key>(?<name>[A-Za-z0-9_]{1,16}))\[/(?<addr>.+)\] logged in with entity id ",
            left: @"\[Server thread/INFO\]: (?<key>(?<name>[A-Za-z0-9_]{1,16})) lost connection: (?<reason>.*)"));

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);
        ingester.IngestOnce(_root);

        File.AppendAllText(log,
            "[19:31:29] [User Authenticator #1/INFO]: UUID of player Flysenberg is 7e7f5dfd-ea66-47a7-8d60-6ee0d0b1e39f\n" +
            "[19:31:30] [Server thread/INFO]: Flysenberg[/192.168.1.127:55072] logged in with entity id 62 at (3068.5350361164747, 112.5, -2296.557766017725)\n" +
            "[19:31:30] [Server thread/INFO]: Flysenberg joined the game\n");
        ingester.IngestOnce(_root);

        // The UUID line and the chat broadcast are not the pair — exactly one join.
        Assert.Single(rec.Calls);
        Assert.Equal("instance_player_joined", rec.Calls[0].Type);
        Assert.Equal("minecraft-test", rec.Calls[0].String("InstanceName"));
        Assert.True(rec.Calls[0].IsNull("PlayerId"));
        Assert.Equal("Flysenberg", rec.Calls[0].String("PlayerName"));
        Assert.Equal("192.168.1.127:55072", rec.Calls[0].String("PlayerAddr"));
        Assert.Equal("Flysenberg", rec.Calls[0].String("SessionKey"));

        File.AppendAllText(log,
            "[19:32:18] [Server thread/INFO]: Flysenberg lost connection: Disconnected\n" +
            "[19:32:18] [Server thread/INFO]: Flysenberg left the game\n");
        ingester.IngestOnce(_root);

        // The leave carries no address of its own — the map supplies the one captured at join.
        Assert.Equal(2, rec.Calls.Count);
        Assert.Equal("instance_player_left", rec.Calls[1].Type);
        Assert.Equal("minecraft-test", rec.Calls[1].String("InstanceName"));
        Assert.True(rec.Calls[1].IsNull("PlayerId"));
        Assert.Equal("Flysenberg", rec.Calls[1].String("PlayerName"));
        Assert.Equal("192.168.1.127:55072", rec.Calls[1].String("PlayerAddr"));
        Assert.Equal("Flysenberg", rec.Calls[1].String("SessionKey"));
        Assert.Equal("Disconnected", rec.Calls[1].String("Reason"));
    }

    [Fact]
    public void Minecraft_a_pre_login_disconnect_by_someone_who_never_joined_emits_nothing()
    {
        // A client that drops during the login handshake is logged with its GameProfile rather than a
        // bare username. There was no join, so there is no leave to attribute — anchoring the username
        // to Minecraft's legal charset at the start of the message is what keeps this line out.
        string log = MakeInstanceWithLog("minecraft", "minecraft-prelogin", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("minecraft-prelogin", log,
            joined: @"\[Server thread/INFO\]: (?<key>(?<name>[A-Za-z0-9_]{1,16}))\[/(?<addr>.+)\] logged in with entity id ",
            left: @"\[Server thread/INFO\]: (?<key>(?<name>[A-Za-z0-9_]{1,16})) lost connection: (?<reason>.*)"));

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);
        ingester.IngestOnce(_root);

        File.AppendAllText(log,
            "[19:29:04] [Server thread/INFO]: com.mojang.authlib.GameProfile@4f2b1a[id=7e7f5dfd-ea66-47a7-8d60-6ee0d0b1e39f,name=Flysenberg,properties={},legacy=false] (/192.168.1.127:55071) lost connection: Disconnected\n");
        ingester.IngestOnce(_root);

        Assert.Empty(rec.Calls);
    }

    [Fact]
    public void Inode_change_resets_the_session_map_a_post_reset_bare_leave_is_honestly_skipped()
    {
        // A fresh server session (log rotated / restarted) must wipe every session the map was tracking
        // — a leave that arrives afterwards for a pre-reset key, carrying no identity of its own, has
        // nothing to attribute and must be skipped rather than resolved against stale state.
        string log = MakeInstanceWithLog("corekeeper", "corekeeper-reset-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("corekeeper-reset-test", log,
            joined: @"\[userid:(?<key>\d+)\] player (?<name>.+?) connected",
            left: @"Disconnected from userid:(?<key>\d+) with reason (?<reason>\S+)"));

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);
        ingester.IngestOnce(_root); // primes at EOF of the (empty) file

        File.AppendAllText(log, "[userid:3801603394] player Woltah connected islocalplayer=False\n");
        ingester.IngestOnce(_root);
        Assert.Single(rec.Calls); // the join landed; the session is now tracked

        // Simulate a new server session: the old log is gone, a brand-new (shorter) file takes its
        // place — either a fresh inode or a same-inode reuse, both of which the tail treats as a reset.
        File.Delete(log);
        File.WriteAllText(log, "");
        ingester.IngestOnce(_root); // detects the reset and clears this instance's session map

        // The SAME key reappears in a bare leave line (no name/id of its own) — the map has nothing for
        // it any more.
        File.AppendAllText(log, "Disconnected from userid:3801603394 with reason App_Min\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls); // still just the one join — the post-reset leave was honestly skipped
    }

    // ---- readiness (instance-ready) ------------------------------------------------------------

    [Fact]
    public void Empty_readiness_regex_emits_ready_immediately_on_the_start_edge()
    {
        // No player patterns either — this is the exact "factorio/minecraft/terraria-shaped: a
        // startup_success_regex but no player_*_regex" case the widened enable gate exists for.
        string log = MakeInstanceWithLog("factorio", "factorio-immediate", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("factorio-immediate", log, joined: "", left: "", ready: ""));

        var cgroups = NewCgroups();
        var rec = new RecordingJournal();
        var ingester = NewIngester(rec, fake, cgroups);

        // Not populated yet — no start edge, no ready.
        ingester.IngestOnce(_root);
        Assert.Empty(rec.Calls);

        // The instance starts (cgroup populates) — immediate honest fallback fires right away, with
        // NO dependency on any log line ever being written.
        SetPopulated("factorio-immediate", populated: true);
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls);
        Assert.Equal("instance_ready", rec.Calls[0].Type);
        Assert.Equal("system:watchdog", rec.Calls[0].Actor);
        Assert.Equal("system", rec.Calls[0].Origin);
        Assert.Equal("factorio-immediate", rec.Calls[0].String("InstanceName"));

        // Steady-state ticks while still populated must NOT re-fire.
        ingester.IngestOnce(_root);
        ingester.IngestOnce(_root);
        Assert.Single(rec.Calls);
    }

    [Fact]
    public void NonEmpty_readiness_regex_fires_once_on_first_match_and_rearms_on_restart()
    {
        string log = MakeInstanceWithLog("factorio", "factorio-ready", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("factorio-ready", log, joined: "", left: "", ready: @"Hosting game at IP ADDR:\d+"));

        var cgroups = NewCgroups();
        var rec = new RecordingJournal();
        var ingester = NewIngester(rec, fake, cgroups);

        // Start edge: populated, but the ready line hasn't been logged yet — no emit.
        SetPopulated("factorio-ready", populated: true);
        ingester.IngestOnce(_root);
        Assert.Empty(rec.Calls);

        // The ready line appears — the normal tail catches it on the next pass.
        File.AppendAllText(log, "1234.567 Hosting game at IP ADDR:34197\n");
        ingester.IngestOnce(_root);
        Assert.Single(rec.Calls);
        Assert.Equal("instance_ready", rec.Calls[0].Type);
        Assert.Equal("factorio-ready", rec.Calls[0].String("InstanceName"));

        // Further lines (even matching ones, e.g. a save-then-relog message) must not re-fire within
        // the same run.
        File.AppendAllText(log, "1234.999 Hosting game at IP ADDR:34197\n");
        ingester.IngestOnce(_root);
        Assert.Single(rec.Calls);

        // A crash-restart: the cgroup drains (stop) then repopulates (respawn) — a genuine new run.
        SetPopulated("factorio-ready", populated: false);
        ingester.IngestOnce(_root);
        SetPopulated("factorio-ready", populated: true);
        File.AppendAllText(log, "0001.000 Hosting game at IP ADDR:34197\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count); // re-armed — a second instance-ready for the new run
        Assert.Equal("instance_ready", rec.Calls[1].Type);
    }

    [Fact]
    public void Readiness_pattern_with_no_player_patterns_is_not_skipped()
    {
        // Requirement (c): factorio/minecraft/terraria-shaped instances have a startup_success_regex
        // but no player_*_regex — they must still be watched and detected, not skipped by the old
        // "no player patterns -> skip" gate.
        string log = MakeInstanceWithLog("minecraft", "mc-ready", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("mc-ready", log, joined: "", left: "", ready: @"Done \([\d.]+s\)! For help"));

        var cgroups = NewCgroups();
        var rec = new RecordingJournal();
        var ingester = NewIngester(rec, fake, cgroups);

        SetPopulated("mc-ready", populated: true);
        ingester.IngestOnce(_root);
        File.AppendAllText(log, "[12:00:00] [Server thread/INFO]: Done (12.345s)! For help, type \"help\"\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls);
        Assert.Equal("instance_ready", rec.Calls[0].Type);
        Assert.Equal("mc-ready", rec.Calls[0].String("InstanceName"));
    }

    [Fact]
    public void Late_attach_to_an_already_ready_instance_emits_ready_once_via_the_whole_file_scan()
    {
        // Requirement (d) / gotcha 6: the ingester's FIRST-ever observation of this instance finds it
        // already populated AND its ready line already logged (a daemon hot-swap/restart mid-boot, or
        // attaching to an instance that was already running) — primeAtEnd would otherwise make the
        // tail skip straight past the line, so the one-shot whole-file scan must catch it.
        string log = MakeInstanceWithLog("factorio", "factorio-late", "1000.000 Loading map\n1000.500 Hosting game at IP ADDR:34197\n1001.000 Player list updated\n");
        var fake = new FakeInstanceService();
        fake.Add(Native("factorio-late", log, joined: "", left: "", ready: @"Hosting game at IP ADDR:\d+"));

        var cgroups = NewCgroups();
        SetPopulated("factorio-late", populated: true); // already running before the ingester ever looked
        var rec = new RecordingJournal();
        var ingester = NewIngester(rec, fake, cgroups);

        ingester.IngestOnce(_root); // first-ever tick: start edge (null -> true) + whole-file scan

        Assert.Single(rec.Calls);
        Assert.Equal("instance_ready", rec.Calls[0].Type);
        Assert.Equal("factorio-late", rec.Calls[0].String("InstanceName"));

        // Steady state — no re-fire, and the tail itself still primes at EOF (no replayed presence lines
        // to worry about here since there are no player patterns in this test).
        ingester.IngestOnce(_root);
        Assert.Single(rec.Calls);
    }

    [Fact]
    public void Invalid_readiness_pattern_with_a_player_pattern_present_never_fires_readiness_but_presence_still_works()
    {
        // A broken readiness regex must disable ONLY readiness (honest — never fabricated), while
        // leaving an unrelated, valid player pattern on the same instance fully functional (mirrors
        // NativeLogMatcherTests.Invalid_pattern_is_disabled_and_warned_never_throws's "the other
        // detection still works" pattern, one layer up).
        string log = MakeInstanceWithLog("factorio", "factorio-badregex", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("factorio-badregex", log,
            joined: @"\[JOIN\] (?<name>\S+)", left: "", ready: "(?<unterminated"));

        var cgroups = NewCgroups();
        SetPopulated("factorio-badregex", populated: true);
        var rec = new RecordingJournal();
        var ingester = NewIngester(rec, fake, cgroups);

        ingester.IngestOnce(_root);
        File.AppendAllText(log, "[JOIN] Alice\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls); // presence still fires...
        Assert.Equal("instance_player_joined", rec.Calls[0].Type); // ...but never instance-ready
    }

    [Fact]
    public void Second_fresh_spawn_does_not_resurrect_run_1s_stale_ready_line_after_SpawnEngine_rotates_the_log()
    {
        // THE BUG this test guards: SpawnEngine used to append to the same log path forever, so on the
        // 2nd+ start of an instance, NativeReadinessMatcher.MatchesExistingContent's whole-file
        // late-attach scan re-read run 1's already-logged ready line and fired instance-ready
        // IMMEDIATELY on run 2's start edge — collapsing the honest "Starting" window. The fix rotates
        // the log to a fresh inode on every fresh spawn (SpawnEngine.RotateLogFile); this test drives
        // that helper directly at the point SpawnEngine.Spawn calls it (the fork itself needs a real
        // cgroup and is exercised live, not here — see SpawnEngineTests for RotateLogFile's own unit
        // coverage).
        string log = MakeInstanceWithLog("factorio", "factorio-rotate", "1000.500 Hosting game at IP ADDR:34197\n");
        var fake = new FakeInstanceService();
        fake.Add(Native("factorio-rotate", log, joined: "", left: "", ready: @"Hosting game at IP ADDR:\d+"));

        var cgroups = NewCgroups();
        var rec = new RecordingJournal();
        var ingester = NewIngester(rec, fake, cgroups);

        // Run 1: this instance is observed already populated with its ready line already logged (the
        // late-attach case another test already covers) — legitimately fires once.
        SetPopulated("factorio-rotate", populated: true);
        ingester.IngestOnce(_root);
        Assert.Single(rec.Calls);
        Assert.Equal("instance_ready", rec.Calls[0].Type);

        // Run 1 ends: the cgroup drains.
        SetPopulated("factorio-rotate", populated: false);
        ingester.IngestOnce(_root);

        // A real crash-restart / manual start now calls SpawnEngine.Spawn, which rotates the log BEFORE
        // the fresh process starts writing — simulate that exact step. Run 1's stale ready line moves
        // out from under `log` into the logs directory.
        string logsDir = Path.Combine(Path.GetDirectoryName(log) ?? "", "..", "logs");
        Directory.CreateDirectory(logsDir);
        new SpawnEngine(cgroups, NullLogger<SpawnEngine>.Instance).RotateLogFile(log, logsDir);
        Assert.False(File.Exists(log), "the rotated-away log must not still be sitting at the old path");

        // Run 2's start edge fires (cgroup repopulates) before the new process has written anything —
        // matching the real race (cgroup.procs is written before exec). The whole-file late-attach scan
        // must NOT resurrect run 1's content, because it no longer lives at `log`.
        SetPopulated("factorio-rotate", populated: true);
        ingester.IngestOnce(_root);
        Assert.Single(rec.Calls); // still just the one — run 2 must NOT have fabricated readiness yet

        // The real game then logs ITS OWN fresh ready line for run 2 — the normal tail catches it.
        File.AppendAllText(log, "0001.000 Hosting game at IP ADDR:34197\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        Assert.Equal("instance_ready", rec.Calls[1].Type);
    }

    [Fact]
    public void Player_presence_also_never_replays_a_rotated_away_prior_run_after_a_fresh_spawn()
    {
        // Same fix, the OTHER consumer of the log: a stale join line from run 1 must not resurface as a
        // "new" join on run 2 just because SpawnEngine rotated the log out and a fresh file appeared.
        string log = MakeInstanceWithLog("factorio", "factorio-rotate-presence",
            "[JOIN] StaleFromRun1 (1) joined the game\n");
        var fake = new FakeInstanceService();
        fake.Add(Native("factorio-rotate-presence", log, Joined, Left));

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec.Journal, fake);
        ingester.IngestOnce(_root); // primes at EOF — run 1's stale join is skipped, as today

        string logsDir = Path.Combine(Path.GetDirectoryName(log) ?? "", "..", "logs");
        Directory.CreateDirectory(logsDir);
        new SpawnEngine(NewCgroups(), NullLogger<SpawnEngine>.Instance).RotateLogFile(log, logsDir);
        Assert.False(File.Exists(log));

        // Run 2 starts and a genuinely new join appears in the fresh file.
        File.AppendAllText(log, "[JOIN] FreshFromRun2 (2) joined the game\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls);
        Assert.Equal("instance_player_joined", rec.Calls[0].Type);
        Assert.Equal("FreshFromRun2", rec.Calls[0].String("PlayerName"));
    }

    // ---- readiness releases the spawn's memory reservation -------------------------------------

    [Fact]
    public void Ready_releases_the_memory_reservation_the_spawn_took()
    {
        // Readiness is the release signal for the whole ledger: the declared memory has materialised by
        // the time a game says it is up, so from here MemAvailable accounts for it and continuing to
        // subtract a reservation would double-count.
        string log = MakeInstanceWithLog("factorio", "factorio-res", "");
        var fake = new FakeInstanceService();
        var spec = Native("factorio-res", log, joined: "", left: "", ready: @"Hosting game at IP ADDR:\d+");
        spec.MemoryCapMb = 4096;
        fake.Add(spec);

        var gate = TestMemoryGate.Posed(availableMb: 10_000);
        Assert.Equal(MemoryGate.Verdict.Allowed, gate.TryReserve("factorio-res", spec).Verdict);

        var cgroups = NewCgroups();
        var ingester = NewIngester(new RecordingJournal(), fake, cgroups, gate);

        SetPopulated("factorio-res", populated: true);
        ingester.IngestOnce(_root);
        Assert.Equal(4096, gate.OutstandingReservedMb()); // started, but not yet ready — still committed

        File.AppendAllText(log, "1234.567 Hosting game at IP ADDR:34197\n");
        ingester.IngestOnce(_root);

        Assert.Equal(0, gate.OutstandingReservedMb());
    }

    [Fact]
    public void Immediate_readiness_releases_the_reservation_on_the_start_edge()
    {
        // A blueprint with no startup_success_regex has no distinct readiness signal, so "ready" is
        // "observed started" — the reservation has nothing else to wait for and is released on the same
        // rule, rather than being held for a signal that will never come.
        string log = MakeInstanceWithLog("factorio", "factorio-res-immediate", "");
        var fake = new FakeInstanceService();
        var spec = Native("factorio-res-immediate", log, joined: "", left: "", ready: "");
        spec.MemoryCapMb = 4096;
        fake.Add(spec);

        var gate = TestMemoryGate.Posed(availableMb: 10_000);
        gate.TryReserve("factorio-res-immediate", spec);

        var cgroups = NewCgroups();
        var ingester = NewIngester(new RecordingJournal(), fake, cgroups, gate);

        ingester.IngestOnce(_root); // not populated yet — no start edge, so nothing is released
        Assert.Equal(4096, gate.OutstandingReservedMb());

        SetPopulated("factorio-res-immediate", populated: true);
        ingester.IngestOnce(_root);

        Assert.Equal(0, gate.OutstandingReservedMb());
    }

    [Fact]
    public void A_readiness_pattern_that_does_not_compile_never_releases_the_reservation()
    {
        // A non-empty startup_success_regex that fails to compile is a real blueprint bug: readiness
        // detection is disabled rather than silently downgraded to the immediate rule, so no
        // instance-ready will ever arrive for this instance and nothing here can release its
        // reservation. The gate's own backstop is the only thing that does.
        string log = MakeInstanceWithLog("factorio", "factorio-res-badregex", "");
        var fake = new FakeInstanceService();
        var spec = Native("factorio-res-badregex", log, joined: "", left: "", ready: "((((unclosed");
        spec.MemoryCapMb = 4096;
        fake.Add(spec);

        var gate = TestMemoryGate.Posed(availableMb: 10_000);
        gate.TryReserve("factorio-res-badregex", spec);

        var rec = new RecordingJournal();
        var cgroups = NewCgroups();
        var ingester = NewIngester(rec, fake, cgroups, gate);

        SetPopulated("factorio-res-badregex", populated: true);
        ingester.IngestOnce(_root);
        File.AppendAllText(log, "1234.567 Hosting game at IP ADDR:34197\n");
        ingester.IngestOnce(_root);
        ingester.IngestOnce(_root);

        Assert.Empty(rec.Calls);                          // no readiness was fabricated…
        Assert.Equal(4096, gate.OutstandingReservedMb()); // …so the reservation stands until the backstop
    }

    // ---- helpers ------------------------------------------------------------------------------

    private NativePlayerPresenceIngester NewIngester(
        WatchdogJournal journal, IInstanceService instances, CgroupManager? cgroups = null, MemoryGate? memoryGate = null)
        => new(new WatchdogOptions { InstancesDir = _root }, instances, journal, TestState.Sessions(),
            cgroups ?? NewCgroups(), memoryGate ?? TestMemoryGate.Disabled(),
            NullLogger<NativePlayerPresenceIngester>.Instance);

    [Fact]
    public void A_config_read_before_the_install_finished_is_re_read_on_the_first_run()
    {
        // The bug this exists for: an instance INSTALLED while the daemon runs is discovered the moment
        // its directory appears — seconds before kgsm finishes writing its config — so the first read
        // sees no startup_success_regex and no log file, and the watch settles on immediate-readiness.
        // Without a re-read that instance then claims to be ready the instant it spawns, for the rest of
        // the daemon's life, whatever its blueprint says. The Control Panel shows that as a server stuck
        // on "Starting": the ready lands BEFORE the start it belongs to, so nothing is left to close the
        // window.
        string log = MakeInstanceWithLog("factorio", "factorio-fresh", "");
        var fake = new FakeInstanceService();
        // What kgsm reports mid-install: the instance exists, its detection config does not yet.
        fake.Add(Native("factorio-fresh", log: "", joined: "", left: "", ready: ""));

        var cgroups = NewCgroups();
        var rec = new RecordingJournal();
        var ingester = NewIngester(rec, fake, cgroups);

        ingester.IngestOnce(_root);          // watch built from the half-written config
        Assert.Empty(rec.Calls);

        // The install completes and kgsm writes the real config.
        fake.Add(Native("factorio-fresh", log, joined: "", left: "", ready: @"Hosting game at IP ADDR"));

        // The game spawns. Readiness must now be judged by the pattern, NOT declared immediately.
        SetPopulated("factorio-fresh", populated: true);
        ingester.IngestOnce(_root);
        Assert.Empty(rec.Calls);

        // …and fires when the game actually says it is up.
        File.AppendAllText(log, "   0.890 Hosting game at IP ADDR:({0.0.0.0:34197})\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls);
        Assert.Equal("instance_ready", rec.Calls[0].Type);
    }

    private static Instance Native(string name, string log, string joined, string left, string ready = "") => new()
    {
        Name = name,
        Runtime = InstanceRuntime.Native,
        LogFile = log,
        PlayerJoinedRegex = joined,
        PlayerLeftRegex = left,
        StartupSuccessRegex = ready,
    };

    // Create <root>/<blueprint>/<instance>/ (for discovery) + the log file inside it, seeded with `seed`.
    private string MakeInstanceWithLog(string blueprint, string instance, string seed)
    {
        string dir = Path.Combine(_root, blueprint, instance);
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, $"{instance}.log");
        File.WriteAllText(log, seed);
        return log;
    }

    /// <summary>A fake <see cref="IInstanceService"/> answering only <see cref="GetInstanceInfo"/> from a map;
    /// every other member throws (the ingester calls nothing else).</summary>
    private sealed class FakeInstanceService : IInstanceService
    {
        private readonly Dictionary<string, Instance> _byName = new(StringComparer.Ordinal);
        public void Add(Instance i) => _byName[i.Name] = i;

        public Instance? GetInstanceInfo(string instanceName)
            => _byName.TryGetValue(instanceName, out var i) ? i : null;

        // ---- unused by the ingester ----
        public Dictionary<string, Instance> GetAll() => throw new NotImplementedException();
        public Dictionary<string, Instance>? GetAllOrNull() => throw new NotImplementedException();
        public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
        public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => throw new NotImplementedException();
        public KgsmResult Install(string blueprintName, string? library = null, string? version = null, string? displayName = null, string? actor = null, string? origin = null, int? port = null, bool? start = null, string? id = null) => throw new NotImplementedException();
        public KgsmResult Move(string instanceName, string library, bool skipSpaceCheck = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult Announce(string instanceName, string message, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult SetDisplayName(string instanceId, string displayName, string? actor = null, string? origin = null) => throw new NotImplementedException();
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
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null, string? reason = null, string? retention = null) => throw new NotImplementedException();
        public KgsmResult PinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult UnpinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public List<TheKrystalShip.KGSM.Core.Models.InstanceConfigEntry>? GetInstanceConfig(string instanceName, bool settableOnly = false) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, TheKrystalShip.KGSM.Core.Models.Enums.LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    
    public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
}

    /// <summary>Records every <see cref="IEventManagementService.EmitWithProvenance"/> call (mirrors the container ingester test's fake).</summary>
}
