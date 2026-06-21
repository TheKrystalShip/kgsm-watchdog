using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the bounded last-N scrollback read backing <c>GET /console/{name}?tail=N</c>: an empty file,
/// fewer-than-N lines, more-than-N (only the last N, oldest-first), a trailing partial line with no
/// newline, an absent file (honest empty, never a throw), CRLF normalisation, and the N=0 / clamp
/// boundaries.
/// </summary>
public sealed class ConsoleTailReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public ConsoleTailReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kgsm-wd-console-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "console.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Empty_file_yields_no_lines()
    {
        File.WriteAllText(_file, string.Empty);
        Assert.Empty(ConsoleTailReader.ReadLastLines(_file, 200));
    }

    [Fact]
    public void Absent_file_yields_no_lines_not_a_throw()
    {
        Assert.Empty(ConsoleTailReader.ReadLastLines(Path.Combine(_dir, "does-not-exist.log"), 200));
    }

    [Fact]
    public void Fewer_than_N_returns_all_lines_in_order()
    {
        File.WriteAllText(_file, "a\nb\nc\n");
        Assert.Equal(new[] { "a", "b", "c" }, ConsoleTailReader.ReadLastLines(_file, 200));
    }

    [Fact]
    public void More_than_N_returns_only_the_last_N_oldest_first()
    {
        File.WriteAllText(_file, "1\n2\n3\n4\n5\n");
        Assert.Equal(new[] { "3", "4", "5" }, ConsoleTailReader.ReadLastLines(_file, 3));
    }

    [Fact]
    public void Exactly_N_returns_all_N()
    {
        File.WriteAllText(_file, "x\ny\nz\n");
        Assert.Equal(new[] { "x", "y", "z" }, ConsoleTailReader.ReadLastLines(_file, 3));
    }

    [Fact]
    public void Trailing_partial_line_without_newline_is_returned()
    {
        // The game wrote mid-line (no terminating '\n') — it must still appear in the scrollback.
        File.WriteAllText(_file, "done-1\ndone-2\npartial-no-newline");
        Assert.Equal(
            new[] { "done-1", "done-2", "partial-no-newline" },
            ConsoleTailReader.ReadLastLines(_file, 200));
    }

    [Fact]
    public void Trailing_partial_counts_toward_the_N_window()
    {
        File.WriteAllText(_file, "old\nmid\ntail-partial");
        // N=2 must keep the two MOST-RECENT lines, including the partial.
        Assert.Equal(new[] { "mid", "tail-partial" }, ConsoleTailReader.ReadLastLines(_file, 2));
    }

    [Fact]
    public void Crlf_lines_are_normalised()
    {
        File.WriteAllText(_file, "win-1\r\nwin-2\r\n");
        Assert.Equal(new[] { "win-1", "win-2" }, ConsoleTailReader.ReadLastLines(_file, 200));
    }

    [Fact]
    public void Zero_or_negative_N_yields_no_lines()
    {
        File.WriteAllText(_file, "a\nb\nc\n");
        Assert.Empty(ConsoleTailReader.ReadLastLines(_file, 0));
        Assert.Empty(ConsoleTailReader.ReadLastLines(_file, -5));
    }

    [Fact]
    public void Reads_while_the_file_is_open_for_append()
    {
        // The game holds the log open with '>>' — the reader must use FileShare.ReadWrite, so an
        // open writer must not block the read.
        using var writer = new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var sw = new StreamWriter(writer) { AutoFlush = true };
        sw.Write("live-1\nlive-2\n");

        Assert.Equal(new[] { "live-1", "live-2" }, ConsoleTailReader.ReadLastLines(_file, 200));
    }
}
