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
        Assert.Equal(new[] { "factorio-test", "76561198000000000", "Alice" }, join.Parameters);

        // ...then a leave; the tail cursor resumes, so only the new line emits.
        File.AppendAllText(log, "2026-06-20 12:05:00 [LEAVE] Alice (76561198000000000) left the game\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        Assert.Equal("instance-player-left", rec.Calls[1].EventType);
        Assert.Equal(new[] { "factorio-test", "76561198000000000", "Alice" }, rec.Calls[1].Parameters);
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
