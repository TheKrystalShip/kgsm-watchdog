using System.Text;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// One window of an append-only log: the lines it holds, and the byte range they came from.
/// <para>
/// <b><see cref="Start"/> is a cursor, and that is the point.</b> A caller reading further back asks
/// for the window ending at the <see cref="Start"/> it was given, so paging is exact while the game
/// keeps appending — a line count from the end would name a different line on every request, and the
/// page after it would silently overlap or skip. <see cref="Start"/> of 0 means the run begins here
/// and there is nothing earlier.
/// </para>
/// </summary>
internal readonly record struct ConsoleWindow(IReadOnlyList<string> Lines, long Start, long End)
{
    public static ConsoleWindow Empty(long end = 0) => new([], end, end);
}

/// <summary>
/// One-shot bounded read of an append-only log file — the finite scrollback half of the console
/// surface (the live half is <see cref="EventChannelTail"/> with <c>primeAtEnd</c>). Backs
/// <c>GET /console/{name}?tail=N&amp;end=OFFSET</c>.
/// <para>
/// <b>Backward block read.</b> The window wanted is always at the END of the file, so the read starts
/// there and walks back a block at a time until it has the lines asked for — the cost is the size of
/// the window, not the size of the log, which is what lets a caller page back through a run of any
/// length. Splitting on <c>'\n'</c> is safe before decoding because a newline byte cannot occur inside
/// a multi-byte UTF-8 sequence. The game is appending concurrently via the launcher's <c>&gt;&gt;</c>,
/// so the file is opened <see cref="FileShare.ReadWrite"/> exactly as <see cref="EventChannelTail"/>
/// opens it, and every window is measured against the length observed when it was opened.
/// </para>
/// <para>
/// <b>Honesty.</b> An absent file (a never-started / stopped instance whose log does not exist yet) is
/// not an error — it returns an empty window, never throws, never fabricates a line. A trailing partial
/// line (no terminating newline — the game wrote mid-line) is returned as its own line; CRLF is
/// normalised to LF-stripped like the live tail.
/// </para>
/// </summary>
internal static class ConsoleTailReader
{
    private const int BlockSize = 64 * 1024;

    /// <summary>
    /// How much of the file one window may read while looking for its first line break. It bounds a
    /// single request's memory against a log with no newlines in it at all; the window it returns
    /// still starts at a real byte offset, so a caller can keep paging back through such a file.
    /// </summary>
    private const long WindowByteBudget = 16L * 1024 * 1024;

    /// <summary>
    /// Return the last <paramref name="maxLines"/> complete-or-trailing lines of <paramref name="path"/>,
    /// oldest-first. Empty when the file is absent, unreadable, empty, or <paramref name="maxLines"/> ≤ 0.
    /// </summary>
    public static IReadOnlyList<string> ReadLastLines(string path, int maxLines) =>
        ReadWindow(path, maxLines, endOffset: -1).Lines;

    /// <summary>
    /// The <paramref name="maxLines"/> lines ending at <paramref name="endOffset"/> (exclusive),
    /// oldest-first, with the byte range they occupy. A negative <paramref name="endOffset"/> means
    /// the end of the file as it stands when this call opens it; an offset past the end is clamped to
    /// it, so a cursor taken before a rotation cannot read past the new file.
    /// </summary>
    public static ConsoleWindow ReadWindow(string path, int maxLines, long endOffset)
    {
        if (maxLines <= 0 || string.IsNullOrEmpty(path))
            return ConsoleWindow.Empty(endOffset < 0 ? 0 : endOffset);

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long end = endOffset < 0 ? fs.Length : Math.Min(endOffset, fs.Length);
            if (end <= 0)
                return ConsoleWindow.Empty(end < 0 ? 0 : end);

            // Walk back a block at a time until the buffer holds one more line break than the window
            // needs — that break is where the window starts.
            var blocks = new List<byte[]>();
            long pos = end;
            int breaks = 0;
            long read = 0;
            while (pos > 0 && breaks <= maxLines && read < WindowByteBudget)
            {
                int size = (int)Math.Min(BlockSize, pos);
                pos -= size;
                read += size;
                fs.Seek(pos, SeekOrigin.Begin);
                byte[] block = new byte[size];
                fs.ReadExactly(block, 0, size);
                foreach (byte b in block)
                    if (b == (byte)'\n')
                        breaks++;
                blocks.Add(block);
            }

            blocks.Reverse();
            byte[] bytes = Concat(blocks);
            return Split(bytes, baseOffset: pos, maxLines, end);
        }
        catch (FileNotFoundException)
        {
            return ConsoleWindow.Empty(); // never-started / stopped instance — honest empty, not an error
        }
        catch (DirectoryNotFoundException)
        {
            return ConsoleWindow.Empty();
        }
        catch (IOException)
        {
            return ConsoleWindow.Empty(); // transient read failure — empty rather than throw
        }
    }

    private static byte[] Concat(List<byte[]> blocks)
    {
        int total = 0;
        foreach (byte[] b in blocks)
            total += b.Length;

        byte[] all = new byte[total];
        int at = 0;
        foreach (byte[] b in blocks)
        {
            Buffer.BlockCopy(b, 0, all, at, b.Length);
            at += b.Length;
        }
        return all;
    }

    /// <summary>
    /// Take the last <paramref name="maxLines"/> lines out of a buffer whose first byte sits at
    /// <paramref name="baseOffset"/> in the file, and report where in the file that window begins.
    /// </summary>
    private static ConsoleWindow Split(byte[] bytes, long baseOffset, int maxLines, long end)
    {
        int len = bytes.Length;
        if (len == 0)
            return ConsoleWindow.Empty(end);

        // A newline at the very end TERMINATES the last line rather than opening an empty one after it.
        int scanFrom = bytes[len - 1] == (byte)'\n' ? len - 2 : len - 1;

        int start = 0;
        int found = 0;
        for (int i = scanFrom; i >= 0; i--)
        {
            if (bytes[i] != (byte)'\n')
                continue;
            if (++found < maxLines)
                continue;
            start = i + 1;
            break;
        }

        string text = Encoding.UTF8.GetString(bytes, start, len - start);
        string[] split = text.Split('\n');
        // The terminating newline leaves an empty final element; a body that ends mid-line does not.
        int count = split.Length > 0 && split[^1].Length == 0 ? split.Length - 1 : split.Length;
        if (count <= 0)
            return ConsoleWindow.Empty(end);

        var lines = new string[count];
        for (int i = 0; i < count; i++)
            lines[i] = split[i].TrimEnd('\r');

        return new ConsoleWindow(lines, baseOffset + start, end);
    }
}
