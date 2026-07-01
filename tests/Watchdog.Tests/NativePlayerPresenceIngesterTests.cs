using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the native ingester end-to-end through one ingest pass (against real temp files + faked
/// kgsm-lib seams): first-attach-at-EOF skips a pre-existing append-only log's history, appended join/left
/// lines emit with the right name/provenance/param order, a non-native instance and a native instance
/// with no patterns are skipped, and the tail cursor resumes across passes (no redelivery).
/// <para>
/// Also covers the player-presence contract §4 correlation + dedup, fed the REAL log lines from all
/// four validated games (matcher + <see cref="PlayerSessionMap"/> + <see cref="EventChannelTail"/>
/// together): stationeers (self-identifying), romestead (addr-correlated, incl. co-NAT distinct ports),
/// Valheim (doubled join lines + a 6x repeated leave burst, key-correlated), Core Keeper (opaque key +
/// leave reason), and a log-rotation (inode change) resetting an instance's session map.
/// </para>
/// </summary>
public sealed class NativePlayerPresenceIngesterTests : IDisposable
{
    private const string Joined = @"\[JOIN\] (?<name>\S+) \((?<id>\d+)\)";
    private const string Left = @"\[LEAVE\] (?<name>\S+) \((?<id>\d+)\)";

    private readonly string _root;

    public NativePlayerPresenceIngesterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kgsm-wd-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

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

        var rec = new RecordingEvents();
        var ingester = NewIngester(rec, fake);

        // Pass 1: primes at EOF → the stale join is skipped, nothing emitted.
        ingester.IngestOnce(_root);
        Assert.Empty(rec.Calls);

        // A real join arrives after we attached.
        File.AppendAllText(log, "2026-06-20 12:00:00 [JOIN] Alice (76561198000000000) joined the game\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls);
        var join = rec.Calls[0];
        Assert.Equal("instance-player-joined", join.EventType);
        Assert.Equal("system", join.Actor);
        Assert.Equal("system", join.Origin);
        // instance, id, name, addr, sessionKey (5 positional params, contract §1) — no addr in this
        // pattern, so sessionKey falls back to id (key ?? addr ?? id ?? name).
        Assert.Equal(
            new[] { "factorio-test", "76561198000000000", "Alice", "", "76561198000000000" },
            join.Parameters);

        // ...then a leave; the tail cursor resumes, so only the new line emits.
        File.AppendAllText(log, "2026-06-20 12:05:00 [LEAVE] Alice (76561198000000000) left the game\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        Assert.Equal("instance-player-left", rec.Calls[1].EventType);
        // instance, id, name, addr, sessionKey, reason (6 positional params) — resolved via the session
        // map (the join's captures), reason empty (this pattern has no reason group).
        Assert.Equal(
            new[] { "factorio-test", "76561198000000000", "Alice", "", "76561198000000000", "" },
            rec.Calls[1].Parameters);
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

        var rec = new RecordingEvents();
        var ingester = NewIngester(rec, fake);

        ingester.IngestOnce(_root);
        File.AppendAllText(log, "[JOIN] Nope (1) joined\n");
        ingester.IngestOnce(_root);

        Assert.Empty(rec.Calls);
    }

    [Fact]
    public void Native_instance_without_patterns_is_skipped()
    {
        string log = MakeInstanceWithLog("terraria", "tw-1", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("tw-1", log, joined: "", left: "")); // detection disabled (honest unknown)

        var rec = new RecordingEvents();
        var ingester = NewIngester(rec, fake);

        ingester.IngestOnce(_root);
        File.AppendAllText(log, "[JOIN] Nope (1) joined\n");
        ingester.IngestOnce(_root);

        Assert.Empty(rec.Calls);
    }

    [Fact]
    public void Both_null_capture_is_dropped_not_emitted()
    {
        // A pattern with no id/name group: a match with neither capture must not reach the wire.
        string log = MakeInstanceWithLog("factorio", "factorio-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("factorio-test", log, joined: "a player connected", left: ""));

        var rec = new RecordingEvents();
        var ingester = NewIngester(rec, fake);

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

        var rec = new RecordingEvents();
        var ingester = NewIngester(rec, fake);
        ingester.IngestOnce(_root); // primes at EOF

        File.AppendAllText(log, "16:23:51: Client Heisen (76561198144397568) is ready\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls);
        Assert.Equal("instance-player-joined", rec.Calls[0].EventType);
        Assert.Equal(
            new[] { "stationeers-test", "76561198144397568", "Heisen", "", "76561198144397568" },
            rec.Calls[0].Parameters);

        File.AppendAllText(log,
            "16:24:23: Client disconnected: 684548920970441496 | Heisen      connectTime: 58.9s, ClientId: 76561198144397568\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        Assert.Equal("instance-player-left", rec.Calls[1].EventType);
        // Resolved via the map (self-identifying here, so it matches what the leave line itself carries).
        Assert.Equal(
            new[] { "stationeers-test", "76561198144397568", "Heisen", "", "76561198144397568", "" },
            rec.Calls[1].Parameters);
    }

    [Fact]
    public void Romestead_addr_correlated_leave_resolves_name_and_conat_sessions_stay_distinct()
    {
        string log = MakeInstanceWithLog("romestead", "romestead-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("romestead-test", log,
            joined: @"Character '(?<name>[^']+)' \(Peer \d+ - (?<addr>[\d.]+:\d+)\) logged in",
            left: @"Peer (?<addr>[\d.]+:\d+) disconnected"));

        var rec = new RecordingEvents();
        var ingester = NewIngester(rec, fake);
        ingester.IngestOnce(_root);

        // Two players behind the same NAT gateway (co-NAT) — same IP, distinct ports.
        File.AppendAllText(log,
            "Character 'Aelia' (Peer 0 - 86.191.216.57:58845) logged in with external id '', assigned to player id 1\n" +
            "Character 'Brutus' (Peer 1 - 86.191.216.57:53376) logged in with external id '', assigned to player id 2\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        Assert.Equal(
            new[] { "romestead-test", "", "Aelia", "86.191.216.57:58845", "86.191.216.57:58845" },
            rec.Calls[0].Parameters);
        Assert.Equal(
            new[] { "romestead-test", "", "Brutus", "86.191.216.57:53376", "86.191.216.57:53376" },
            rec.Calls[1].Parameters);

        // Aelia's bare-addr leave must resolve her name — and must not disturb Brutus's session.
        File.AppendAllText(log, "Peer 86.191.216.57:58845 disconnected - RemoteConnectionClose\n");
        ingester.IngestOnce(_root);

        Assert.Equal(3, rec.Calls.Count);
        Assert.Equal("instance-player-left", rec.Calls[2].EventType);
        Assert.Equal(
            new[] { "romestead-test", "", "Aelia", "86.191.216.57:58845", "86.191.216.57:58845", "" },
            rec.Calls[2].Parameters);

        // Brutus's own leave still resolves independently — the co-NAT sessions never collided.
        File.AppendAllText(log, "Peer 86.191.216.57:53376 disconnected - RemoteConnectionClose\n");
        ingester.IngestOnce(_root);

        Assert.Equal(4, rec.Calls.Count);
        Assert.Equal(
            new[] { "romestead-test", "", "Brutus", "86.191.216.57:53376", "86.191.216.57:53376", "" },
            rec.Calls[3].Parameters);
    }

    [Fact]
    public void Valheim_doubled_join_lines_dedup_and_a_6x_repeated_leave_burst_dedups_key_correlated()
    {
        string log = MakeInstanceWithLog("valheim", "valheim-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("valheim-test", log,
            joined: @"Got character ZDOID from (?<name>.+?) : (?<key>\d+):\d+",
            left: @"Destroying abandoned non persistent zdo \d+:\d+ owner (?<key>\d+)"));

        var rec = new RecordingEvents();
        var ingester = NewIngester(rec, fake);
        ingester.IngestOnce(_root);

        // Every real Valheim line appears twice: a Console-wrapped form and a bare form.
        File.AppendAllText(log,
            "07/01/2026 16:56:10: Console: [Info   :   Unity Log] Got character ZDOID from Test : 651023867:1\n" +
            "07/01/2026 16:56:10: Got character ZDOID from Test : 651023867:1\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls); // the doubled line dedups to exactly one join
        Assert.Equal("instance-player-joined", rec.Calls[0].EventType);
        Assert.Equal(new[] { "valheim-test", "", "Test", "", "651023867" }, rec.Calls[0].Parameters);

        // The cleanup burst re-logs the same disconnect up to 6x.
        for (int i = 0; i < 6; i++)
            File.AppendAllText(log, "07/01/2026 16:56:21: Destroying abandoned non persistent zdo 651023867:1 owner 651023867\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count); // exactly one left; the other 5 deduped via evict
        Assert.Equal("instance-player-left", rec.Calls[1].EventType);
        Assert.Equal(new[] { "valheim-test", "", "Test", "", "651023867", "" }, rec.Calls[1].Parameters);
    }

    [Fact]
    public void Corekeeper_opaque_key_join_and_leave_with_reason_resolve_and_evict()
    {
        string log = MakeInstanceWithLog("corekeeper", "corekeeper-test", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("corekeeper-test", log,
            joined: @"\[userid:(?<key>\d+)\] player (?<name>.+?) connected",
            left: @"Disconnected from userid:(?<key>\d+) with reason (?<reason>\S+)"));

        var rec = new RecordingEvents();
        var ingester = NewIngester(rec, fake);
        ingester.IngestOnce(_root);

        File.AppendAllText(log, "[userid:3801603394] player Woltah connected islocalplayer=False\n");
        ingester.IngestOnce(_root);

        Assert.Single(rec.Calls);
        Assert.Equal(new[] { "corekeeper-test", "", "Woltah", "", "3801603394" }, rec.Calls[0].Parameters);

        File.AppendAllText(log, "Disconnected from userid:3801603394 with reason App_Min\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        Assert.Equal("instance-player-left", rec.Calls[1].EventType);
        Assert.Equal(new[] { "corekeeper-test", "", "Woltah", "", "3801603394", "App_Min" }, rec.Calls[1].Parameters);
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

        var rec = new RecordingEvents();
        var ingester = NewIngester(rec, fake);
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

    // ---- helpers ------------------------------------------------------------------------------

    private NativePlayerPresenceIngester NewIngester(IEventManagementService events, IInstanceService instances)
        => new(new WatchdogOptions { InstancesDir = _root }, instances, events, NullLogger<NativePlayerPresenceIngester>.Instance);

    private static Instance Native(string name, string log, string joined, string left) => new()
    {
        Name = name,
        Runtime = InstanceRuntime.Native,
        LogFile = log,
        PlayerJoinedRegex = joined,
        PlayerLeftRegex = left,
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
        public KgsmResult Install(string blueprintName, string? installDir = null, string? version = null, string? name = null, string? actor = null, string? origin = null, int? port = null) => throw new NotImplementedException();
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
        public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
        public KgsmResult Save(string instanceName) => throw new NotImplementedException();
        public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
        public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
        public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, TheKrystalShip.KGSM.Core.Models.Enums.LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    /// <summary>Records every <see cref="IEventManagementService.EmitWithProvenance"/> call (mirrors the container ingester test's fake).</summary>
    private sealed class RecordingEvents : IEventManagementService
    {
        public readonly record struct Call(string EventType, string? Actor, string? Origin, string[] Parameters);
        public List<Call> Calls { get; } = [];

        public KgsmResult EmitWithProvenance(string eventType, string? actor, string? origin, params string[] parameters)
        {
            Calls.Add(new Call(eventType, actor, origin, parameters));
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
