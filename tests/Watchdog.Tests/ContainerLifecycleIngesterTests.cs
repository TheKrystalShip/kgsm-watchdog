using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the container lifecycle ingester's discovery (two-level walk, through the instance symlink —
/// mirrors <see cref="PlayerPresenceIngesterTests"/>), instance-name derivation, instances-dir
/// resolution, and the end-to-end ingest pass against real temp files + a real (but never-shelling)
/// <see cref="UpnpService"/>: every test instance has <c>EnablePortForwarding=false</c> or an empty
/// port set, so <see cref="UpnpService.ApplyAsync"/>'s own gate returns <c>Skipped</c> BEFORE it would
/// ever spawn the real <c>upnpc</c> process — the same boundary <c>UpnpServiceTests</c> exploits — so
/// the suite stays fast, deterministic, and independent of whether <c>upnpc</c>/an IGD are present.
/// </summary>
public sealed class ContainerLifecycleIngesterTests : IDisposable
{
    private readonly string _root;

    public ContainerLifecycleIngesterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "kgsm-wd-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- instance-name derivation ------------------------------------------------------------

    [Theory]
    [InlineData("/data/kgsm/instances/vrising/vr-1/events/lifecycle.ndjson", "vr-1")]
    [InlineData("/data/kgsm/instances/empyrion/emp-07/events/lifecycle.ndjson", "emp-07")]
    public void Derives_instance_name_from_the_dir_two_levels_above_the_file(string path, string expected)
    {
        Assert.Equal(expected, ContainerLifecycleIngester.DeriveInstanceName(path));
    }

    // ---- discovery ----------------------------------------------------------------------------

    [Fact]
    public void Discovers_two_level_channels_and_ignores_instances_without_one()
    {
        string has = MakeChannel("vrising", "vr-1", "");
        MakeInstanceDir("vrising", "vr-other"); // no events/lifecycle.ndjson -> ignored
        string has2 = MakeChannel("empyrion", "emp-1", "");

        var found = ContainerLifecycleIngester.DiscoverChannels(_root).ToHashSet();

        Assert.Contains(has, found);
        Assert.Contains(has2, found);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Discovers_a_channel_reached_through_an_instance_symlink()
    {
        string blueprintDir = Path.Combine(_root, "vrising");
        Directory.CreateDirectory(blueprintDir);

        string workingDir = Path.Combine(_root, "_real", "vrising", "vr-1");
        Directory.CreateDirectory(Path.Combine(workingDir, "events"));
        File.WriteAllText(Path.Combine(workingDir, "events", "lifecycle.ndjson"), "");

        string symlink = Path.Combine(blueprintDir, "vr-1");
        Directory.CreateSymbolicLink(symlink, workingDir);

        var found = ContainerLifecycleIngester.DiscoverChannels(_root).ToList();

        string expected = Path.Combine(symlink, "events", "lifecycle.ndjson");
        Assert.Contains(expected, found);
        Assert.Equal("vr-1", ContainerLifecycleIngester.DeriveInstanceName(expected));
    }

    [Fact]
    public void Missing_root_discovers_nothing_without_throwing()
    {
        Assert.Empty(ContainerLifecycleIngester.DiscoverChannels(Path.Combine(_root, "nope")));
    }

    // ---- instances-dir resolution -------------------------------------------------------------

    [Fact]
    public void Explicit_instances_dir_option_wins()
    {
        var ingester = NewIngester(new WatchdogOptions { InstancesDir = "/custom/instances" }, new FakeInstanceService());
        Assert.Equal("/custom/instances", ingester.ResolveInstancesDir());
    }

    // ---- end-to-end ingest pass (real UpnpService, gated to Skipped — never shells upnpc) -----

    [Fact]
    public async Task Container_instance_started_then_stopping_processes_without_throwing()
    {
        string channel = MakeChannel("vrising", "vr-1", "");
        var fake = new FakeInstanceService();
        fake.Add(Container("vr-1"));

        var ingester = NewIngester(new WatchdogOptions { InstancesDir = _root }, fake);

        File.AppendAllText(channel, """{"type":"instance_started","ts":"t"}""" + "\n");
        await ingester.IngestOnceAsync(_root);
        Assert.Equal(1, fake.CallCount); // resolved exactly once for the new line

        File.AppendAllText(channel, """{"type":"instance_stopping","ts":"t"}""" + "\n");
        await ingester.IngestOnceAsync(_root);
        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task Appended_lines_are_not_redelivered_on_a_second_pass()
    {
        string channel = MakeChannel("vrising", "vr-2", """{"type":"instance_started","ts":"t"}""" + "\n");
        var fake = new FakeInstanceService();
        fake.Add(Container("vr-2"));

        var ingester = NewIngester(new WatchdogOptions { InstancesDir = _root }, fake);

        await ingester.IngestOnceAsync(_root);
        Assert.Equal(1, fake.CallCount);

        await ingester.IngestOnceAsync(_root); // nothing new appended — no redelivery
        Assert.Equal(1, fake.CallCount);

        File.AppendAllText(channel, """{"type":"instance_stopping","ts":"t"}""" + "\n");
        await ingester.IngestOnceAsync(_root);
        Assert.Equal(2, fake.CallCount); // only the new line resolved
    }

    [Fact]
    public async Task Native_instance_is_resolved_but_never_faults_the_pass()
    {
        // Runtime.Native must short-circuit before any UpnpService call — asserting the pass completes
        // cleanly (no throw) for a channel that, by construction, should never exist for a native
        // instance in production (defensive path, not load-bearing).
        string channel = MakeChannel("factorio", "ftest", "");
        var fake = new FakeInstanceService();
        fake.Add(Native("ftest"));

        var ingester = NewIngester(new WatchdogOptions { InstancesDir = _root }, fake);

        File.AppendAllText(channel, """{"type":"instance_started","ts":"t"}""" + "\n");
        await ingester.IngestOnceAsync(_root);

        Assert.Equal(1, fake.CallCount); // still resolved (to learn the runtime), just not acted on
    }

    [Fact]
    public async Task Unresolvable_instance_is_skipped_without_throwing()
    {
        string channel = MakeChannel("vrising", "vr-unknown", "");
        var fake = new FakeInstanceService(); // no instance registered -> GetInstanceInfo returns null

        var ingester = NewIngester(new WatchdogOptions { InstancesDir = _root }, fake);

        File.AppendAllText(channel, """{"type":"instance_started","ts":"t"}""" + "\n");
        await ingester.IngestOnceAsync(_root);

        Assert.Equal(1, fake.CallCount); // attempted, then honestly gave up (retried next tick, not cached)
    }

    [Fact]
    public async Task Malformed_line_is_dropped_before_any_instance_lookup()
    {
        string channel = MakeChannel("vrising", "vr-bad", "");
        var fake = new FakeInstanceService();
        fake.Add(Container("vr-bad"));

        var ingester = NewIngester(new WatchdogOptions { InstancesDir = _root }, fake);

        File.AppendAllText(channel, "not json at all\n");
        await ingester.IngestOnceAsync(_root);

        Assert.Equal(0, fake.CallCount); // parser dropped it before GetInstanceInfo was ever called
    }

    // ---- helpers ------------------------------------------------------------------------------

    private ContainerLifecycleIngester NewIngester(WatchdogOptions options, IInstanceService instances)
        => new(options, instances, new UpnpService(NullLogger<UpnpService>.Instance),
            new NoClaims(), NullLogger<ContainerLifecycleIngester>.Instance);

    /// <summary>No other instance claims any port, so a close here releases whatever it declares.</summary>
    private sealed class NoClaims : IForwardedPortClaims
    {
        public IReadOnlySet<(int Port, string Protocol)> ForwardedPortsHeldByOthers(string excluding)
            => new HashSet<(int, string)>();
    }

    private static Instance Container(string name) => new()
    {
        Name = name,
        Runtime = InstanceRuntime.Container,
        EnablePortForwarding = false, // gate returns Skipped before UpnpService ever shells upnpc
        Ports = [],
    };

    private static Instance Native(string name) => new()
    {
        Name = name,
        Runtime = InstanceRuntime.Native,
        EnablePortForwarding = false,
    };

    private string MakeInstanceDir(string blueprint, string instance)
    {
        string dir = Path.Combine(_root, blueprint, instance);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string MakeChannel(string blueprint, string instance, string contents)
    {
        string instanceDir = MakeInstanceDir(blueprint, instance);
        string eventsDir = Path.Combine(instanceDir, "events");
        Directory.CreateDirectory(eventsDir);
        string channel = Path.Combine(eventsDir, "lifecycle.ndjson");
        File.WriteAllText(channel, contents);
        return channel;
    }

    /// <summary>A fake <see cref="IInstanceService"/> answering only <see cref="GetInstanceInfo"/> from a
    /// map and counting calls (so a test can assert exactly-once resolution / no-redelivery); every
    /// other member throws (the ingester calls nothing else).</summary>
    private sealed class FakeInstanceService : IInstanceService
    {
        private readonly Dictionary<string, Instance> _byName = new(StringComparer.Ordinal);
        public int CallCount { get; private set; }
        public void Add(Instance i) => _byName[i.Name] = i;

        public Instance? GetInstanceInfo(string instanceName)
        {
            CallCount++;
            return _byName.TryGetValue(instanceName, out var i) ? i : null;
        }

        // ---- unused by the ingester ----
        public Dictionary<string, Instance> GetAll() => throw new NotImplementedException();
        public Dictionary<string, Instance>? GetAllOrNull() => throw new NotImplementedException();
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
        public KgsmResult CheckUpdate(string instanceName, bool emit = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
        public KgsmResult DeleteBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
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
        public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, TheKrystalShip.KGSM.Core.Models.Enums.LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    
    public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
}
}
