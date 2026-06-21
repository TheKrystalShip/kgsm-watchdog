using System.Text;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// One-shot bounded read of the <em>last ≤N lines</em> of an append-only log file — the finite
/// scrollback half of the console surface (the live half is <see cref="EventChannelTail"/> with
/// <c>primeAtEnd</c>). Used by <c>GET /console/{name}?tail=N</c>.
/// <para>
/// <b>Why forward-stream + ring buffer, not a backward block-seek.</b> At this scale (a handful of
/// instances, an occasional scrollback request) a backward seek's UTF-8-boundary bookkeeping buys
/// nothing; a single forward pass that keeps only the last <c>N</c> lines in a bounded queue is
/// simpler and never holds more than <c>N</c> lines in memory — in keeping with this daemon's
/// footprint discipline (Workstation GC, ConserveMemory). The game is appending concurrently via the
/// launcher's <c>&gt;&gt;</c>, so the file is opened <see cref="FileShare.ReadWrite"/> exactly as
/// <see cref="EventChannelTail"/> opens it.
/// </para>
/// <para>
/// <b>Honesty.</b> An absent file (a never-started / stopped instance whose log does not exist yet) is
/// not an error — it returns an empty list, never throws, never fabricates a line. A trailing partial
/// line (no terminating newline — the game wrote mid-line) is returned as its own line; CRLF is
/// normalised to LF-stripped like the live tail.
/// </para>
/// </summary>
internal static class ConsoleTailReader
{
    /// <summary>
    /// Return the last <paramref name="maxLines"/> complete-or-trailing lines of <paramref name="path"/>,
    /// oldest-first. Empty when the file is absent, unreadable, empty, or <paramref name="maxLines"/> ≤ 0.
    /// Reads at most <paramref name="maxLines"/> lines into memory (bounded ring buffer).
    /// </summary>
    public static IReadOnlyList<string> ReadLastLines(string path, int maxLines)
    {
        if (maxLines <= 0 || string.IsNullOrEmpty(path))
            return [];

        // Ring buffer: enqueue every line, drop the front once we exceed the budget, so only the last
        // N survive regardless of file size.
        var ring = new Queue<string>(Math.Min(maxLines, 1024));

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                ring.Enqueue(line);
                if (ring.Count > maxLines)
                    ring.Dequeue();
            }
        }
        catch (FileNotFoundException)
        {
            return []; // never-started / stopped instance — honest empty, not an error
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (IOException)
        {
            return []; // transient read failure — empty rather than throw
        }

        return ring.Count == 0 ? [] : ring.ToArray();
    }
}
