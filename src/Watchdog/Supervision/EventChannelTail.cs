using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using TheKrystalShip.KGSM.Watchdog.Interop;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Tails one append-only NDJSON event-channel file by <b>(inode, byte-offset)</b>, the resume key the
/// frozen contract mandates. Each <see cref="ReadNewLines"/> call returns the complete lines appended
/// since the previous call and advances the offset; a partial trailing line (no newline yet) is left
/// for the next call so a half-written record is never parsed.
/// <para>
/// <b>Rotation handling.</b> The in-container shim creates a <em>fresh file (new inode)</em> on each
/// container start, so a changed inode means "new session" → re-read from offset 0. Inode is read with
/// <see cref="NativeMethods.statx"/> (following symlinks — the path traverses the kgsm
/// instance→working_dir symlink). <b>Belt-and-suspenders:</b> inode numbers can be <em>reused</em>
/// after a delete on some filesystems (empirically observed on ext4: an immediate delete+recreate of
/// the same path reused the inode), which would defeat a pure inode check. So a shrink —
/// <c>current length &lt; last offset</c> — ALSO forces a re-read from 0. This is strictly additive to
/// the inode check, not a replacement: a fresh inode is still the primary trigger, and the shrink check
/// only partially covers same-inode reuse, so it does not reintroduce the size-comparison race the
/// contract warns against (that race was about using size <em>instead of</em> inode to detect a fresh
/// file mid-write).
/// </para>
/// <para>
/// <b>Honest residual.</b> The shrink check catches a same-inode recreate <em>only</em> when the new
/// file is shorter than the prior offset at the next poll. A same-inode recreate that has already grown
/// back to ≥ the old offset before the poll (a busy server writing within the sub-second window) would
/// resume mid-stream and miss the gap. That window is narrow and the frozen contract only asked for
/// inode-keying; closing it fully would need a birth-time (<c>statx STATX_BTIME</c>) signal,
/// deliberately not added here.
/// </para>
/// <para>
/// <b>Absent file.</b> A channel that does not exist yet (the <c>events/</c> dir exists from instance
/// creation but <c>events.ndjson</c> only appears once the container first starts and the shim writes)
/// is not an error: <see cref="ReadNewLines"/> returns nothing and the tracker resets, so it is picked
/// up from offset 0 the moment it appears. The same holds if a file vanishes and later returns.
/// </para>
/// <para>Not thread-safe by design — one tail per file, driven by a single poll loop.</para>
/// </summary>
internal sealed class EventChannelTail(string path)
{
    /// <summary>The absolute channel path this tail follows (used for instance-name derivation + logging).</summary>
    public string Path { get; } = path;

    private ulong? _inode;
    private long _offset;

    // Reused across reads so a chunk that ends mid-line (no trailing newline) is carried to the next
    // read instead of being emitted as a truncated record.
    private readonly StringBuilder _pending = new();

    /// <summary>The inode currently being followed (null before the first successful read / when absent). For tests + diagnostics.</summary>
    public ulong? CurrentInode => _inode;

    /// <summary>The byte offset read up to in the current file. For tests + diagnostics.</summary>
    public long CurrentOffset => _offset;

    /// <summary>
    /// Read every complete line appended since the last call, advancing the offset. Returns an empty
    /// list when the file is absent, unreadable, or has no new complete line. Resets to offset 0 on an
    /// inode change (fresh container session) or a shrink (same-inode reuse safety net).
    /// </summary>
    public IReadOnlyList<string> ReadNewLines()
    {
        ulong? inode = TryReadInode(Path);
        if (inode is null)
        {
            // Absent / unreadable: forget where we were so a re-appearance is read from the start.
            ResetTo(null);
            return [];
        }

        // Fresh inode ⇒ new session ⇒ start over. (Primary rotation signal per the contract.)
        if (_inode != inode)
            ResetTo(inode);

        long length;
        try
        {
            length = new FileInfo(Path).Length;
        }
        catch (Exception)
        {
            return []; // raced a delete between statx and stat — try again next tick
        }

        // Shrink with an UNCHANGED inode ⇒ same path was truncated/recreated onto a reused inode.
        // Additive safety net (see class remarks) — re-read from 0 rather than seek past EOF and miss
        // every new line.
        if (length < _offset)
        {
            _offset = 0;
            _pending.Clear();
        }

        if (length <= _offset)
            return []; // nothing new

        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);

            // Read the appended bytes into the pending buffer, then carve out complete lines (those
            // terminated by '\n'). Anything after the last newline stays pending for the next read.
            char[] chunk = new char[4096];
            int read;
            while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
                _pending.Append(chunk, 0, read);

            _offset = fs.Position;
        }
        catch (Exception)
        {
            return []; // transient read failure — retry next tick, offset unchanged
        }

        ExtractCompleteLines(lines);
        return lines;
    }

    /// <summary>Pull every newline-terminated line out of the pending buffer, leaving any partial tail behind.</summary>
    private void ExtractCompleteLines(List<string> sink)
    {
        string buffered = _pending.ToString();
        int start = 0;
        int nl;
        while ((nl = buffered.IndexOf('\n', start)) >= 0)
        {
            // Trim a trailing '\r' so CRLF channels parse cleanly; the parser also tolerates blanks.
            int end = nl;
            if (end > start && buffered[end - 1] == '\r')
                end--;
            sink.Add(buffered.Substring(start, end - start));
            start = nl + 1;
        }

        _pending.Clear();
        if (start < buffered.Length)
            _pending.Append(buffered, start, buffered.Length - start); // carry the partial line
    }

    private void ResetTo(ulong? inode)
    {
        _inode = inode;
        _offset = 0;
        _pending.Clear();
    }

    /// <summary>
    /// Read a path's inode via <see cref="NativeMethods.statx"/> (following symlinks), or null when the
    /// path does not exist / cannot be statted. <c>internal static</c> so a test can assert the offset-32
    /// layout against a real file without going through a tail instance.
    /// </summary>
    internal static ulong? TryReadInode(string path)
    {
        byte[] buf = new byte[NativeMethods.StatxBufferSize];
        int rc = NativeMethods.statx(NativeMethods.AT_FDCWD, path, 0, NativeMethods.STATX_INO, buf);
        if (rc != 0)
            return null; // ENOENT (not yet created) or any other stat error — treat as absent
        return BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(NativeMethods.StatxInoOffset, sizeof(ulong)));
    }
}
