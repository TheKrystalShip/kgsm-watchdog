using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Events;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// <c>instance-ready</c> belongs to a RUN, and a daemon start is not one.
/// <para>
/// The ingester detects a start from its own first sight of a populated cgroup, and for an instance that
/// was already up that sight arrives on the first tick after every daemon start. An adopt does not rotate
/// the log, so the running game's own ready line is still sitting in it for the whole-file late-attach
/// scan to find — which is exactly right the first time and a fabricated boot every time after. The latch
/// that separates them is keyed to the run itself and lives on disk, so it outlives the daemon holding it.
/// </para>
/// <para>
/// A "daemon restart" here is a second <see cref="NativePlayerPresenceIngester"/> built over the same
/// state directory and the same cgroup tree — the ingester keeps its in-memory watches in a field, so a
/// fresh instance is precisely what a restarted daemon presents. Run keys come from real pids in
/// <c>cgroup.procs</c> (this test process, and pid 1) because the key is the kernel's own record of when
/// a process started; a fabricated pid has no such record, which is its own case below.
/// </para>
/// </summary>
public sealed class ReadinessSurvivesDaemonRestartTests : IDisposable
{
    private readonly string _root;
    private readonly string _cgroupRoot;
    private readonly string _stateRoot;

    public ReadinessSurvivesDaemonRestartTests()
    {
        string id = Guid.NewGuid().ToString("N");
        _root = Path.Combine(Path.GetTempPath(), "kgsm-wd-ready-" + id);
        _cgroupRoot = Path.Combine(Path.GetTempPath(), "kgsm-wd-ready-cg-" + id);
        _stateRoot = Path.Combine(Path.GetTempPath(), "kgsm-wd-ready-state-" + id);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_cgroupRoot);
        Directory.CreateDirectory(_stateRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_cgroupRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_stateRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_run_a_previous_daemon_announced_is_not_announced_again_when_the_daemon_restarts()
    {
        // A server that has been up for a while, with its ready line long since written to a log no
        // adopt will rotate.
        string log = MakeInstanceWithLog("palworld", "ketchup", "Running Palworld dedicated server on :8211\n");
        var fake = NewFake("ketchup", log, ready: "Running Palworld dedicated server on");
        SetPopulated("ketchup", populated: true);
        SetLeader("ketchup", Environment.ProcessId);

        var first = new RecordingJournal();
        NewIngester(first, fake).IngestOnce(_root);

        Assert.Single(first.Calls);
        Assert.Equal("server.ready", first.Calls[0].Type);

        // The daemon restarts. Same game, same pid, same log, same ready line still in it — and nothing
        // about that is a boot.
        var second = new RecordingJournal();
        NewIngester(second, fake).IngestOnce(_root);
        Assert.Empty(second.Calls);

        // Restarting it repeatedly does not accumulate them either, which is the shape the defect took.
        var third = new RecordingJournal();
        NewIngester(third, fake).IngestOnce(_root);
        Assert.Empty(third.Calls);
    }

    [Fact]
    public void A_genuinely_different_run_is_announced_even_though_a_previous_run_was_already_announced()
    {
        // The latch must not become a mute button: it suppresses one run, not the instance.
        string log = MakeInstanceWithLog("palworld", "ketchup", "Running Palworld dedicated server on :8211\n");
        var fake = NewFake("ketchup", log, ready: "Running Palworld dedicated server on");
        SetPopulated("ketchup", populated: true);
        SetLeader("ketchup", Environment.ProcessId);

        var first = new RecordingJournal();
        NewIngester(first, fake).IngestOnce(_root);
        Assert.Single(first.Calls);

        // The game restarts while the daemon is down: same instance, a different process. pid 1 is a
        // real process with a real, different start tick, so it names a different run.
        SetLeader("ketchup", 1);

        var second = new RecordingJournal();
        NewIngester(second, fake).IngestOnce(_root);

        Assert.Single(second.Calls);
        Assert.Equal("server.ready", second.Calls[0].Type);
        Assert.Equal("ketchup", second.Calls[0].String("InstanceName"));
    }

    [Fact]
    public void A_restart_within_one_daemon_is_still_announced_once_per_run()
    {
        // The in-process latch and the durable one have to agree, or a crash-restart the daemon watched
        // end to end would go unannounced because the durable record still names the run before it.
        string log = MakeInstanceWithLog("factorio", "factorio-1", "");
        var fake = NewFake("factorio-1", log, ready: @"Hosting game at IP ADDR:\d+");
        SetLeader("factorio-1", Environment.ProcessId);
        SetPopulated("factorio-1", populated: true);

        var rec = new RecordingJournal();
        var ingester = NewIngester(rec, fake);

        ingester.IngestOnce(_root);
        File.AppendAllText(log, "0001.000 Hosting game at IP ADDR:34197\n");
        ingester.IngestOnce(_root);
        Assert.Single(rec.Calls);

        // The run ends and a different process takes its place, all under one daemon.
        SetPopulated("factorio-1", populated: false);
        ingester.IngestOnce(_root);
        SetLeader("factorio-1", 1);
        SetPopulated("factorio-1", populated: true);
        ingester.IngestOnce(_root);

        File.AppendAllText(log, "0001.000 Hosting game at IP ADDR:34197\n");
        ingester.IngestOnce(_root);

        Assert.Equal(2, rec.Calls.Count);
        Assert.All(rec.Calls, c => Assert.Equal("server.ready", c.Type));
    }

    [Fact]
    public void A_run_whose_leader_cannot_be_read_is_announced_rather_than_silently_swallowed()
    {
        // An unnameable run cannot be recognised later, so suppressing it would risk losing the
        // announcement entirely. A repeated announcement is the smaller lie than a missing one.
        string log = MakeInstanceWithLog("factorio", "factorio-nopid", "0001.000 Hosting game at IP ADDR:34197\n");
        var fake = NewFake("factorio-nopid", log, ready: @"Hosting game at IP ADDR:\d+");
        SetPopulated("factorio-nopid", populated: true);
        SetLeader("factorio-nopid", int.MaxValue); // no such process, so no start tick to key on

        var first = new RecordingJournal();
        NewIngester(first, fake).IngestOnce(_root);
        Assert.Single(first.Calls);

        var second = new RecordingJournal();
        NewIngester(second, fake).IngestOnce(_root);
        Assert.Single(second.Calls);
    }

    [Fact]
    public void The_announcement_is_recorded_against_the_run_even_when_the_ready_line_arrives_later()
    {
        // The run is named at the start edge; a pattern that only matches minutes into the boot still
        // has to be filed against the run that started, or the record names nothing the next daemon
        // will recognise.
        string log = MakeInstanceWithLog("factorio", "factorio-slow", "");
        var fake = NewFake("factorio-slow", log, ready: @"Hosting game at IP ADDR:\d+");
        SetLeader("factorio-slow", Environment.ProcessId);
        SetPopulated("factorio-slow", populated: true);

        var first = new RecordingJournal();
        var ingester = NewIngester(first, fake);
        ingester.IngestOnce(_root);          // start edge: booting, nothing to match yet
        Assert.Empty(first.Calls);

        File.AppendAllText(log, "0001.000 Hosting game at IP ADDR:34197\n");
        ingester.IngestOnce(_root);          // the game finishes booting
        Assert.Single(first.Calls);

        var second = new RecordingJournal();
        NewIngester(second, fake).IngestOnce(_root);
        Assert.Empty(second.Calls);
    }

    private NativePlayerPresenceIngester NewIngester(WatchdogJournal journal, IInstanceService instances) =>
        new(new WatchdogOptions { InstancesDir = _root }, instances, journal, TestState.Sessions(),
            new CgroupManager(
                new WatchdogOptions { CgroupMountPoint = _cgroupRoot, CgroupBaseName = "kgsm.slice" },
                NullLogger<CgroupManager>.Instance),
            TestMemoryGate.Disabled(),
            TestState.Readiness(new WatchdogOptions { StateFile = Path.Combine(_stateRoot, "desired-state.json") }),
            NullLogger<NativePlayerPresenceIngester>.Instance);

    private static FakeInstanceService NewFake(string name, string log, string ready)
    {
        var fake = new FakeInstanceService();
        fake.Add(new Instance
        {
            Name = name,
            Runtime = InstanceRuntime.Native,
            LogFile = log,
            StartupSuccessRegex = ready,
            PlayerJoinedRegex = "",
            PlayerLeftRegex = "",
        });
        return fake;
    }

    private string MakeInstanceWithLog(string blueprint, string name, string seed)
    {
        string dir = Path.Combine(_root, blueprint, name);
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, name + ".log");
        File.WriteAllText(log, seed);
        return log;
    }

    private void SetPopulated(string instance, bool populated)
    {
        string dir = Path.Combine(_cgroupRoot, "kgsm.slice", instance);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cgroup.events"), $"populated {(populated ? 1 : 0)}\nfrozen 0\n");
    }

    /// <summary>Put a leader pid in the fake cgroup's <c>cgroup.procs</c>, which is where the run's name
    /// is read from — exactly as <see cref="CgroupManager.FirstPid"/> reads a real one.</summary>
    private void SetLeader(string instance, int pid)
    {
        string dir = Path.Combine(_cgroupRoot, "kgsm.slice", instance);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cgroup.procs"), pid + "\n");
    }
}
