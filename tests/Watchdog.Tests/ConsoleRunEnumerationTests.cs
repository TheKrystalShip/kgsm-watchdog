using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Control;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The run index behind <c>GET /console/{name}/runs</c> and <c>?run=I</c>. Rotation on every fresh
/// spawn means a crash and the restart that followed it live in two different files, so a caller
/// correlating an event against output needs to know which stretch of console is which — and it must
/// get them in true chronological order, from a source that cannot be fooled by a filename.
/// </summary>
public sealed class ConsoleRunEnumerationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-wd-runs-" + Guid.NewGuid().ToString("N"));
    private readonly string _logsDir;

    public ConsoleRunEnumerationTests()
    {
        Directory.CreateDirectory(_dir);
        _logsDir = Path.Combine(_dir, "logs");
        Directory.CreateDirectory(_logsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string LiveLog => Path.Combine(_dir, "romestead.log");

    private Instance Spec(string? logsDir = null) => new()
    {
        Name = "romestead",
        Runtime = InstanceRuntime.Native,
        LogFile = LiveLog,
        LogsDir = logsDir ?? _logsDir,
    };

    /// <summary>Writes a run's log and stamps when it last printed.</summary>
    private string Rotated(string name, DateTime endedUtc, string content = "output\n")
    {
        string path = Path.Combine(_logsDir, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, endedUtc);
        return path;
    }

    [Fact]
    public void OrdersByLastWrite_NotByFilename()
    {
        // A log rotated by an older build is NAMED for the moment it was rotated, not for when its
        // run ended — an instance stopped on the 1st and started on the 5th got the 5th. Sorting on
        // those names interleaves history wrongly, so the order must come from the filesystem.
        Rotated("romestead.2026-07-05T11:47:49.log", new DateTime(2026, 7, 1, 21, 26, 10, DateTimeKind.Utc));
        Rotated("romestead.2026-07-02T08:00:00.log", new DateTime(2026, 7, 2, 8, 0, 0, DateTimeKind.Utc));

        var runs = ConsoleEndpoints.EnumerateRuns(Spec());

        // Newest first: the run that ENDED on the 2nd outranks the one misnamed for the 5th.
        Assert.Equal(2, runs.Count);
        Assert.Equal("romestead.2026-07-02T08:00:00.log", runs[0].File.Name);
        Assert.Equal("romestead.2026-07-05T11:47:49.log", runs[1].File.Name);
    }

    [Fact]
    public void TheLiveLogIsRunZeroAndIsTheOnlyCurrentOne()
    {
        Rotated("romestead.2026-08-11T18:16:47.log", new DateTime(2026, 8, 11, 18, 16, 38, DateTimeKind.Utc));
        File.WriteAllText(LiveLog, "Starting server\nServer ready...\n");

        var runs = ConsoleEndpoints.EnumerateRuns(Spec());

        Assert.Equal(2, runs.Count);
        Assert.True(runs[0].Current);
        Assert.Equal("romestead.log", runs[0].File.Name);
        Assert.False(runs[1].Current);
    }

    [Fact]
    public void WithNoLiveLog_TheNewestRotatedRunLeads_AndNothingClaimsToBeCurrent()
    {
        // The window between a run ending and the next spawn: rotation has happened, so the live path
        // does not exist. There is history to read and no run in progress — reporting one would be
        // the fabrication this surface exists to avoid.
        Rotated("romestead.2026-08-11T18:16:47.log", new DateTime(2026, 8, 11, 18, 16, 38, DateTimeKind.Utc));

        var runs = ConsoleEndpoints.EnumerateRuns(Spec());

        Assert.Single(runs);
        Assert.False(runs[0].Current);
    }

    [Fact]
    public void AnInstanceThatHasNeverRun_HasNoRuns()
    {
        var runs = ConsoleEndpoints.EnumerateRuns(Spec());

        Assert.Empty(runs);
    }

    [Fact]
    public void WithNoLogsDir_TheLiveLogIsNotListedTwice()
    {
        // RotateLogFile falls back to the log file's own directory when logs_dir is empty, so the
        // reader looks there too — and must not pick the live log back up as one of its own siblings.
        File.WriteAllText(LiveLog, "Server ready...\n");
        string sibling = Path.Combine(_dir, "romestead.2026-08-11T18:16:47.log");
        File.WriteAllText(sibling, "prior run\n");

        var runs = ConsoleEndpoints.EnumerateRuns(Spec(logsDir: ""));

        Assert.Equal(2, runs.Count);
        Assert.Single(runs, r => r.Current);
        Assert.Equal(2, runs.Select(r => r.File.FullName).Distinct().Count());
    }

    [Fact]
    public void UnrelatedFilesInTheLogsDirectoryAreNotRuns()
    {
        // Some games write their own logs beside KGSM's. A run is a file this daemon rotated there,
        // and nothing else — listing a game's own log as a "run" would attach a spawn/exit meaning
        // to a file that has none.
        Rotated("romestead.2026-08-11T18:16:47.log", new DateTime(2026, 8, 11, 18, 16, 38, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(_logsDir, "latest.log"), "the game's own log\n");
        File.WriteAllText(Path.Combine(_logsDir, "crash-2026-08-11.txt"), "a game crash report\n");

        var runs = ConsoleEndpoints.EnumerateRuns(Spec());

        Assert.Single(runs);
        Assert.Equal("romestead.2026-08-11T18:16:47.log", runs[0].File.Name);
    }

    [Fact]
    public void TheCollisionSuffixedNameIsStillARun()
    {
        // A crash loop can end two runs inside one second; the rotator then appends a tick suffix.
        // That file is a run like any other and must not fall out of the index.
        Rotated("romestead.2026-08-11T18:16:47.log", new DateTime(2026, 8, 11, 18, 16, 47, DateTimeKind.Utc));
        Rotated("romestead.2026-08-11T18:16:47.638234567.log", new DateTime(2026, 8, 11, 18, 16, 48, DateTimeKind.Utc));

        var runs = ConsoleEndpoints.EnumerateRuns(Spec());

        Assert.Equal(2, runs.Count);
        Assert.Equal("romestead.2026-08-11T18:16:47.638234567.log", runs[0].File.Name);
    }
}
