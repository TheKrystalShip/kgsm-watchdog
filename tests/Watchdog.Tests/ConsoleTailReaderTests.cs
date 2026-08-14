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

    // ---- windows + the cursor that pages back through them ----

    [Fact]
    public void Window_reports_the_byte_range_it_served()
    {
        File.WriteAllText(_file, "aaa\nbbb\nccc\n");   // 4 bytes per line
        ConsoleWindow w = ConsoleTailReader.ReadWindow(_file, 2, endOffset: -1);

        Assert.Equal(new[] { "bbb", "ccc" }, w.Lines);
        Assert.Equal(4, w.Start);    // "bbb" begins at byte 4
        Assert.Equal(12, w.End);     // the whole file
    }

    [Fact]
    public void Window_start_is_zero_when_the_run_begins_there()
    {
        File.WriteAllText(_file, "one\ntwo\n");
        ConsoleWindow w = ConsoleTailReader.ReadWindow(_file, 50, endOffset: -1);

        Assert.Equal(new[] { "one", "two" }, w.Lines);
        Assert.Equal(0, w.Start);    // nothing earlier to read
    }

    [Fact]
    public void Paging_on_the_cursor_walks_the_whole_file_exactly_once()
    {
        // 1000 lines, read back 100 at a time. Every line must appear exactly once and in order —
        // no overlap, no gap. This is the property a line-count offset cannot hold.
        var written = Enumerable.Range(0, 1000).Select(i => "line-" + i).ToArray();
        File.WriteAllText(_file, string.Join('\n', written) + "\n");

        var seen = new List<string>();
        long cursor = -1;
        for (int page = 0; page < 20; page++)
        {
            ConsoleWindow w = ConsoleTailReader.ReadWindow(_file, 100, cursor);
            if (w.Lines.Count == 0)
                break;
            seen.InsertRange(0, w.Lines);
            if (w.Start == 0)
                break;
            cursor = w.Start;
        }

        Assert.Equal(written, seen);
    }

    [Fact]
    public void A_cursor_still_names_the_same_line_after_the_game_appends()
    {
        // The whole reason the cursor is a byte offset: the file grows between the two reads, and the
        // second must still return the lines immediately BEFORE the first, not lines shifted by
        // however much arrived in between.
        File.WriteAllText(_file, "a\nb\nc\nd\n");
        ConsoleWindow first = ConsoleTailReader.ReadWindow(_file, 2, endOffset: -1);
        Assert.Equal(new[] { "c", "d" }, first.Lines);

        File.AppendAllText(_file, "e\nf\ng\n");

        ConsoleWindow earlier = ConsoleTailReader.ReadWindow(_file, 2, first.Start);
        Assert.Equal(new[] { "a", "b" }, earlier.Lines);
        Assert.Equal(0, earlier.Start);
    }

    [Fact]
    public void An_end_offset_past_the_file_is_clamped_to_it()
    {
        // A cursor taken before a rotation must not read past the end of the file that replaced it.
        File.WriteAllText(_file, "x\ny\n");
        ConsoleWindow w = ConsoleTailReader.ReadWindow(_file, 10, endOffset: 999_999);

        Assert.Equal(new[] { "x", "y" }, w.Lines);
        Assert.Equal(4, w.End);
    }

    [Fact]
    public void Window_spanning_many_blocks_is_read_whole()
    {
        // Bigger than the 64 KiB backward block, so the reader has to stitch blocks together — and a
        // line must never be split across the seam.
        var written = Enumerable.Range(0, 5000).Select(i => "l" + i + new string('x', 40)).ToArray();
        File.WriteAllText(_file, string.Join('\n', written) + "\n");

        ConsoleWindow w = ConsoleTailReader.ReadWindow(_file, 5000, endOffset: -1);
        Assert.Equal(written, w.Lines);
        Assert.Equal(0, w.Start);
    }

    [Fact]
    public void Window_of_an_absent_file_is_empty_not_a_throw()
    {
        ConsoleWindow w = ConsoleTailReader.ReadWindow(Path.Combine(_dir, "nope.log"), 10, -1);
        Assert.Empty(w.Lines);
        Assert.Equal(0, w.Start);
    }

    [Fact]
    public void Multibyte_utf8_survives_a_backward_read()
    {
        // Splitting on '\n' BEFORE decoding is only safe because a newline byte cannot occur inside a
        // multi-byte UTF-8 sequence — this is the test that says so.
        File.WriteAllText(_file, "héllo — ünïcode\n日本語のログ\nemoji 🎮 line\n");
        ConsoleWindow w = ConsoleTailReader.ReadWindow(_file, 2, endOffset: -1);

        Assert.Equal(new[] { "日本語のログ", "emoji 🎮 line" }, w.Lines);
    }
}
