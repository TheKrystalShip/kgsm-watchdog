using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the (inode, byte-offset) tail-and-resume logic against real temp files: that a tail returns
/// only complete (newline-terminated) lines, resumes from its offset on subsequent reads, re-reads
/// from 0 when the inode changes (the shim's fresh-file-per-session contract) and — additively — when
/// the file shrinks under a reused inode, and treats an absent-then-appearing file as a from-0 read.
/// Also pins the statx inode read (offset 32) against the OS so a layout regression is loud, not silent.
/// </summary>
public sealed class EventChannelTailTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public EventChannelTailTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kgsm-wd-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "events.ndjson");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- statx inode read --------------------------------------------------------------------

    [Fact]
    public void Statx_reads_the_same_inode_the_OS_reports()
    {
        File.WriteAllText(_file, "x\n");

        ulong? viaStatx = EventChannelTail.TryReadInode(_file);
        Assert.NotNull(viaStatx);

        // The kernel's own view via .NET's UnixFileStatus is not exposed, so cross-check against the
        // coreutils `stat -L` inode — proving the offset-32 layout is correct on this architecture.
        string statOut = RunStat(_file);
        Assert.Equal(statOut, viaStatx!.Value.ToString());
    }

    [Fact]
    public void Statx_on_a_missing_path_is_null_not_a_throw()
    {
        Assert.Null(EventChannelTail.TryReadInode(Path.Combine(_dir, "does-not-exist")));
    }

    // ---- basic tailing -----------------------------------------------------------------------

    [Fact]
    public void Reads_only_complete_lines_and_carries_a_partial()
    {
        var tail = new EventChannelTail(_file);

        File.WriteAllText(_file, "line-1\nline-2\npartial-no-newline");
        var first = tail.ReadNewLines();
        Assert.Equal(new[] { "line-1", "line-2" }, first); // the partial is withheld

        File.AppendAllText(_file, "-now-complete\nline-4\n");
        var second = tail.ReadNewLines();
        Assert.Equal(new[] { "partial-no-newline-now-complete", "line-4" }, second);
    }

    [Fact]
    public void Resumes_from_offset_no_redelivery()
    {
        var tail = new EventChannelTail(_file);

        File.AppendAllText(_file, "a\nb\n");
        Assert.Equal(new[] { "a", "b" }, tail.ReadNewLines());

        // No new bytes → nothing, and the offset is unchanged.
        Assert.Empty(tail.ReadNewLines());

        File.AppendAllText(_file, "c\n");
        Assert.Equal(new[] { "c" }, tail.ReadNewLines()); // only the new line, a/b not redelivered
    }

    [Fact]
    public void Strips_trailing_cr_for_crlf_channels()
    {
        var tail = new EventChannelTail(_file);
        File.WriteAllText(_file, "win-1\r\nwin-2\r\n");

        Assert.Equal(new[] { "win-1", "win-2" }, tail.ReadNewLines());
    }

    // ---- rotation: inode change --------------------------------------------------------------

    [Fact]
    public void Inode_change_rereads_from_zero()
    {
        var tail = new EventChannelTail(_file);

        File.WriteAllText(_file, "old-1\nold-2\n");
        Assert.Equal(new[] { "old-1", "old-2" }, tail.ReadNewLines());
        ulong? firstInode = tail.CurrentInode;
        Assert.NotNull(firstInode);

        // Simulate the shim's fresh-file-per-session: replace the path with a brand-new inode. A rename
        // over the path guarantees a different inode (unlike delete+recreate, which can reuse one).
        string replacement = Path.Combine(_dir, "replacement.tmp");
        File.WriteAllText(replacement, "new-1\nnew-2\n");
        File.Move(replacement, _file, overwrite: true);

        var afterRotation = tail.ReadNewLines();
        Assert.Equal(new[] { "new-1", "new-2" }, afterRotation); // re-read from 0, not resumed past EOF
        Assert.NotEqual(firstInode, tail.CurrentInode);          // the inode actually changed
    }

    // ---- rotation: same-inode shrink (the reuse safety net) ----------------------------------

    [Fact]
    public void Shrink_under_same_inode_rereads_from_zero()
    {
        // Drive the tail's internal state directly through a truncation-in-place: same inode, smaller
        // file. The additive shrink check must reset the offset rather than seek past the new EOF.
        var tail = new EventChannelTail(_file);

        File.WriteAllText(_file, "long-line-1\nlong-line-2\nlong-line-3\n");
        Assert.Equal(3, tail.ReadNewLines().Count);
        long offsetBefore = tail.CurrentOffset;
        Assert.True(offsetBefore > 0);

        // Truncate in place (same inode) and write a shorter content whose length < the old offset.
        using (var fs = new FileStream(_file, FileMode.Truncate, FileAccess.Write))
        using (var sw = new StreamWriter(fs))
            sw.Write("short\n");

        var afterShrink = tail.ReadNewLines();
        Assert.Equal(new[] { "short" }, afterShrink); // shrink forced a from-0 re-read, line not missed
    }

    // ---- absent → appears later --------------------------------------------------------------

    [Fact]
    public void Absent_file_yields_nothing_then_reads_from_zero_when_it_appears()
    {
        var tail = new EventChannelTail(_file); // _file does not exist yet

        Assert.Empty(tail.ReadNewLines());
        Assert.Null(tail.CurrentInode);

        File.WriteAllText(_file, "first-after-appear\n");
        Assert.Equal(new[] { "first-after-appear" }, tail.ReadNewLines());
    }

    [Fact]
    public void File_vanishing_then_returning_rereads_from_zero()
    {
        var tail = new EventChannelTail(_file);

        File.WriteAllText(_file, "session-1\n");
        Assert.Equal(new[] { "session-1" }, tail.ReadNewLines());

        File.Delete(_file);
        Assert.Empty(tail.ReadNewLines()); // absent — reset

        File.WriteAllText(_file, "session-2\n");
        Assert.Equal(new[] { "session-2" }, tail.ReadNewLines()); // from 0, not resumed
    }

    private static string RunStat(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("stat", $"-L -c %i \"{path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return outp;
    }
}
