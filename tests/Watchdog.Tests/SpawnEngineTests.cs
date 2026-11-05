using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the pure, security-relevant helpers in the spawn path: the launcher that passes the
/// engine core's resolved arguments to the game verbatim, and
/// <see cref="SpawnEngine.RotateLogFile"/> — the fresh-spawn log rotation that keeps
/// <c>NativeReadinessMatcher.MatchesExistingContent</c>'s whole-file late-attach
/// scan from resurrecting a prior run's stale ready line on an instance's 2nd+ start. (The fork itself
/// needs a real instance + cgroup and is exercised live, not in the unit suite — see
/// <see cref="NativePlayerPresenceIngesterTests"/> for the end-to-end readiness-across-rotation
/// coverage, which drives <c>RotateLogFile</c> directly the way <see cref="SpawnEngine.Spawn"/> does.)
/// </summary>
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
    public async Task The_launcher_hands_every_argument_to_the_game_exactly_as_given()
    {
        // The engine core resolves the arguments; the launcher passes each as one parameter, so a value
        // holding a space, a dollar sign or a glob reaches the game unchanged.
        string log = Path.Combine(_tempDir, "launch.log");
        string input = Path.Combine(_tempDir, "input");
        string cgroupProcs = Path.Combine(_tempDir, "cgroup.procs");
        File.WriteAllText(input, "");

        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardError = true };
        foreach (string argument in new[]
        {
            "-c", SpawnEngine.LauncherBody, "sh", cgroupProcs, _tempDir, input, log, "1024",
            "printf", "%s|", "a b", "$HOME", "*", "$(id)",
        })
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("a b|$HOME|*|$(id)|", File.ReadAllText(log));
        Assert.True(int.Parse(File.ReadAllText(cgroupProcs).Trim(), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public void A_bare_executable_that_sits_in_the_launch_directory_is_run_from_there()
    {
        File.WriteAllText(Path.Combine(_tempDir, "game"), "#!/bin/sh\n");

        Assert.Equal("./game", SpawnEngine.ExecutablePath("game", _tempDir));
        Assert.Equal("sh", SpawnEngine.ExecutablePath("sh", _tempDir));
        Assert.Equal("/opt/game/bin/game", SpawnEngine.ExecutablePath("/opt/game/bin/game", _tempDir));
    }

    [Fact]
    public void ValidateSpawnable_reports_a_launch_argument_that_could_not_be_resolved()
    {
        string? msg = SpawnEngine.ValidateSpawnable(new InstanceSpec
        {
            Name = "factorio-test",
            ExecutableFile = "factorio",
            SocketFile = "/run/f.in",
            LogFile = "/var/log/f.log",
            InstallDir = "/opt/f",
            LaunchArgumentsProblem = "no value for {bogus}",
        });

        Assert.Equal("factorio-test: launch arguments: no value for {bogus}", msg);
    }

    [Fact]
    public void ValidateSpawnable_empty_descriptor_reports_no_usable_info_not_a_blank_field()
    {
        // The bug: an unpopulated descriptor (no name) used to fall through to the per-field check and
        // throw "{name}: executable_file is empty" with a BLANK name → the crash alert read
        // "start failed: : executable_file is empty" (a missing field). It must now name the real cause
        // and never produce the empty interpolated field.
        string? msg = SpawnEngine.ValidateSpawnable(new InstanceSpec());

        Assert.Equal("instance descriptor is empty (kgsm returned no usable info)", msg);
        Assert.DoesNotContain(": :", $"start failed: {msg}"); // the doubled colon is gone
    }

    [Fact]
    public void ValidateSpawnable_named_but_missing_exe_keeps_the_name()
    {
        // A real, resolved instance with a genuinely empty executable_file still names itself — the
        // per-field message is honest because the name is present.
        string? msg = SpawnEngine.ValidateSpawnable(
            new InstanceSpec { Name = "factorio-test" });

        Assert.Equal("factorio-test: executable_file is empty", msg);
    }

    [Fact]
    public void ValidateSpawnable_fully_populated_descriptor_is_null()
    {
        var inst = new InstanceSpec
        {
            Name = "factorio-test",
            ExecutableFile = "./factorio",
            SocketFile = "/run/factorio-test.in",
            LogFile = "/var/log/factorio-test.log",
            InstallDir = "/opt/factorio-test",
        };

        Assert.Null(SpawnEngine.ValidateSpawnable(inst));
    }

    // ---- RotateLogFile (fresh-spawn log rotation) ---------------------------------------------

    [Fact]
    public void RotateLogFile_names_the_rotated_file_for_when_the_run_ENDED()
    {
        string logsDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(logsDir);

        string log = Path.Combine(_tempDir, "romestead.log");
        File.WriteAllText(log, "Unhandled exception. System.NullReferenceException\n");

        // The run ended days before this rotation — an instance stopped Monday, started Friday. The
        // name must say when the server last printed, not when the next spawn happened to rotate it.
        var ended = new DateTime(2026, 7, 1, 21, 26, 10, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(log, ended);

        NewEngine().RotateLogFile(log, logsDir);

        string rotated = Assert.Single(Directory.GetFiles(logsDir, "romestead.*.log"));
        Assert.Equal("romestead.2026-07-01T21:26:10.log", Path.GetFileName(rotated));
    }

    [Fact]
    public void RotateLogFile_moves_a_nonempty_prior_run_log_to_logs_dir()
    {
        string logDir = Path.Combine(_tempDir, "work");
        string logsDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(logDir);
        Directory.CreateDirectory(logsDir);

        string log = Path.Combine(logDir, "factorio-test.log");
        File.WriteAllText(log, "1000.500 Hosting game at IP ADDR:34197\nPlayer joined\n");
        ulong? inodeBefore = EventChannelTail.TryReadInode(log);

        NewEngine().RotateLogFile(log, logsDir);

        // The original path either no longer exists, or (if something raced a fresh write in) now
        // points at a NEW inode — either way, the prior run's content is no longer reachable there.
        if (File.Exists(log))
            Assert.NotEqual(inodeBefore, EventChannelTail.TryReadInode(log));
        else
            Assert.False(File.Exists(log));

        // The content itself is preserved in the logs directory — never silently dropped.
        string[] siblings = Directory.GetFiles(logsDir, "factorio-test.*.log");
        Assert.Single(siblings);
        Assert.Equal("1000.500 Hosting game at IP ADDR:34197\nPlayer joined\n", File.ReadAllText(siblings[0]));

        // The working directory should NOT have the rotated log
        Assert.Empty(Directory.GetFiles(logDir, "factorio-test.*.log"));
    }

    [Fact]
    public void RotateLogFile_falls_back_to_log_file_directory_when_logs_dir_is_empty()
    {
        // When logsDir is empty, the method should fall back to the log file's parent directory
        // (backward compatibility for callers that don't have a logs directory)
        string log = Path.Combine(_tempDir, "fallback-test.log");
        File.WriteAllText(log, "some log content\n");

        NewEngine().RotateLogFile(log, string.Empty);

        // Rotated log ends up in the same directory as the log file
        string[] siblings = Directory.GetFiles(_tempDir, "fallback-test.*.log");
        Assert.Single(siblings);
        Assert.Equal("some log content\n", File.ReadAllText(siblings[0]));
    }

    [Fact]
    public void RotateLogFile_is_a_noop_when_the_log_does_not_exist_yet()
    {
        string log = Path.Combine(_tempDir, "never-started.log");
        string logsDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(logsDir);

        NewEngine().RotateLogFile(log, logsDir); // must not throw — first-ever spawn, nothing to rotate

        Assert.False(File.Exists(log));
        Assert.Empty(Directory.GetFiles(logsDir));
    }

    [Fact]
    public void RotateLogFile_is_a_noop_on_an_already_empty_log()
    {
        string log = Path.Combine(_tempDir, "empty.log");
        string logsDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(log, "");

        NewEngine().RotateLogFile(log, logsDir);

        // Left in place at the SAME inode — nothing worth rotating away.
        Assert.True(File.Exists(log));
        Assert.Empty(Directory.GetFiles(logsDir));
    }

    [Fact]
    public void RotateLogFile_never_throws_and_leaves_the_log_in_place_when_the_directory_is_gone()
    {
        string missingDir = Path.Combine(_tempDir, "vanished");
        string log = Path.Combine(missingDir, "x.log");
        string logsDir = Path.Combine(_tempDir, "logs");
        Directory.CreateDirectory(logsDir);
        // Deliberately do NOT create `missingDir` — File.Exists(log) is false, so this is the
        // same "nothing to rotate" no-op path; asserts the best-effort contract never throws.
        var ex = Record.Exception(() => NewEngine().RotateLogFile(log, logsDir));
        Assert.Null(ex);
    }
}
