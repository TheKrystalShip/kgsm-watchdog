using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the pure, security-relevant helpers in the spawn path: the envsubst-equivalent the daemon
/// runs over <c>executable_arguments</c> before the launcher word-splits them, and
/// <see cref="SpawnEngine.RotateLogFile"/> — the fresh-spawn log rotation fix (PLAN.md Increment 9
/// follow-up) that keeps <c>NativeReadinessMatcher.MatchesExistingContent</c>'s whole-file late-attach
/// scan from resurrecting a prior run's stale ready line on an instance's 2nd+ start. (The fork itself
/// needs a real instance + cgroup and is exercised live, not in the unit suite — see
/// <see cref="NativePlayerPresenceIngesterTests"/> for the end-to-end readiness-across-rotation
/// coverage, which drives <c>RotateLogFile</c> directly the way <see cref="SpawnEngine.Spawn"/> does.)
/// </summary>
[Collection(EnvironmentCollection.Name)] // ExpandEnvironment tests mutate process env vars — serialize with the other env-touching classes
public sealed class SpawnEngineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "kgsm-wd-spawn-" + Guid.NewGuid().ToString("N"));

    public SpawnEngineTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private SpawnEngine NewEngine()
    {
        var cgroups = new CgroupManager(
            new WatchdogOptions { CgroupMountPoint = Path.Combine(_tempDir, "cg"), CgroupBaseName = "kgsm.slice" },
            NullLogger<CgroupManager>.Instance);
        return new SpawnEngine(cgroups, NullLogger<SpawnEngine>.Instance);
    }

    [Fact]
    public void ExpandEnvironment_passes_through_when_no_dollar()
    {
        Assert.Equal("--port 1234 --world foo", SpawnEngine.ExpandEnvironment("--port 1234 --world foo"));
    }

    [Fact]
    public void ExpandEnvironment_expands_braced_and_bare_vars()
    {
        Environment.SetEnvironmentVariable("WD_TEST_PORT", "27015");
        Environment.SetEnvironmentVariable("WD_TEST_WORLD", "alpha");
        try
        {
            Assert.Equal("-p 27015", SpawnEngine.ExpandEnvironment("-p ${WD_TEST_PORT}"));
            Assert.Equal("-w alpha end", SpawnEngine.ExpandEnvironment("-w $WD_TEST_WORLD end"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WD_TEST_PORT", null);
            Environment.SetEnvironmentVariable("WD_TEST_WORLD", null);
        }
    }

    [Fact]
    public void ExpandEnvironment_unknown_var_becomes_empty()
    {
        Environment.SetEnvironmentVariable("WD_TEST_ABSENT", null);
        Assert.Equal("a  b", SpawnEngine.ExpandEnvironment("a ${WD_TEST_ABSENT} b"));
    }

    [Fact]
    public void ExpandEnvironment_trailing_dollar_is_literal()
    {
        Assert.Equal("cost$", SpawnEngine.ExpandEnvironment("cost$"));
    }

    [Fact]
    public void ExpandEnvironment_resolves_instance_vars_like_envsubst()
    {
        // The real 7dtd case: args reference $instance_install_dir, which KGSM exports in the bash
        // path. The resolver must map it from the Instance, not blank it from the daemon's env.
        var inst = new TheKrystalShip.KGSM.Core.Models.Instance { InstallDir = "/opt/7dtd/install" };
        var resolve = SpawnEngine.BuildVarResolver(inst);

        Assert.Equal(
            "-dedicated -configfile=/opt/7dtd/install/serverconfig.xml",
            SpawnEngine.ExpandEnvironment("-dedicated -configfile=$instance_install_dir/serverconfig.xml", resolve));
    }

    [Fact]
    public void ValidateSpawnable_empty_descriptor_reports_no_usable_info_not_a_blank_field()
    {
        // The bug: an unpopulated descriptor (no name) used to fall through to the per-field check and
        // throw "{name}: executable_file is empty" with a BLANK name → the crash alert read
        // "start failed: : executable_file is empty" (a missing field). It must now name the real cause
        // and never produce the empty interpolated field.
        string? msg = SpawnEngine.ValidateSpawnable(new TheKrystalShip.KGSM.Core.Models.Instance());

        Assert.Equal("instance descriptor is empty (kgsm returned no usable info)", msg);
        Assert.DoesNotContain(": :", $"start failed: {msg}"); // the doubled colon is gone
    }

    [Fact]
    public void ValidateSpawnable_named_but_missing_exe_keeps_the_name()
    {
        // A real, resolved instance with a genuinely empty executable_file still names itself — the
        // per-field message is honest because the name is present.
        string? msg = SpawnEngine.ValidateSpawnable(
            new TheKrystalShip.KGSM.Core.Models.Instance { Name = "factorio-test" });

        Assert.Equal("factorio-test: executable_file is empty", msg);
    }

    [Fact]
    public void ValidateSpawnable_fully_populated_descriptor_is_null()
    {
        var inst = new TheKrystalShip.KGSM.Core.Models.Instance
        {
            Name = "factorio-test",
            ExecutableFile = "./factorio",
            SocketFile = "/run/factorio-test.in",
            LogFile = "/var/log/factorio-test.log",
            InstallDir = "/opt/factorio-test",
        };

        Assert.Null(SpawnEngine.ValidateSpawnable(inst));
    }

    // ---- RotateLogFile (fresh-spawn log rotation — Increment 9 follow-up) ---------------------

    [Fact]
    public void RotateLogFile_moves_a_nonempty_prior_run_log_to_a_fresh_inode_at_the_same_path()
    {
        string log = Path.Combine(_tempDir, "factorio-test.log");
        File.WriteAllText(log, "1000.500 Hosting game at IP ADDR:34197\nPlayer joined\n");
        ulong? inodeBefore = EventChannelTail.TryReadInode(log);

        NewEngine().RotateLogFile(log);

        // The original path either no longer exists, or (if something raced a fresh write in) now
        // points at a NEW inode — either way, the prior run's content is no longer reachable there.
        if (File.Exists(log))
            Assert.NotEqual(inodeBefore, EventChannelTail.TryReadInode(log));
        else
            Assert.False(File.Exists(log));

        // The content itself is preserved, just moved — never silently dropped.
        string[] siblings = Directory.GetFiles(_tempDir, "factorio-test.*.log");
        Assert.Single(siblings);
        Assert.Equal("1000.500 Hosting game at IP ADDR:34197\nPlayer joined\n", File.ReadAllText(siblings[0]));
    }

    [Fact]
    public void RotateLogFile_is_a_noop_when_the_log_does_not_exist_yet()
    {
        string log = Path.Combine(_tempDir, "never-started.log");

        NewEngine().RotateLogFile(log); // must not throw — first-ever spawn, nothing to rotate

        Assert.False(File.Exists(log));
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void RotateLogFile_is_a_noop_on_an_already_empty_log()
    {
        string log = Path.Combine(_tempDir, "empty.log");
        File.WriteAllText(log, "");

        NewEngine().RotateLogFile(log);

        // Left in place at the SAME inode — nothing worth rotating away.
        Assert.True(File.Exists(log));
        Assert.Single(Directory.GetFiles(_tempDir));
    }

    [Fact]
    public void RotateLogFile_never_throws_and_leaves_the_log_in_place_when_the_directory_is_gone()
    {
        string missingDir = Path.Combine(_tempDir, "vanished");
        string log = Path.Combine(missingDir, "x.log");
        // Deliberately do NOT create `missingDir` — File.Exists(log) is false, so this is the
        // same "nothing to rotate" no-op path; asserts the best-effort contract never throws.
        var ex = Record.Exception(() => NewEngine().RotateLogFile(log));
        Assert.Null(ex);
    }
}
