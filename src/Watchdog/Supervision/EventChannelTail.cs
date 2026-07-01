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
/// <para>
/// <b>Reset signal.</b> <see cref="LastReadResetSession"/> surfaces "this call detected a genuine new
/// session" up to a session-scoped consumer — <see cref="NativePlayerPresenceIngester"/>'s
/// <see cref="PlayerSessionMap"/> clears itself on it, since every prior session is gone with the old
/// file.
/// </para>
/// <para>Not thread-safe by design — one tail per file, driven by a single poll loop.</para>
/// </summary>
internal sealed class EventChannelTail(string path, bool primeAtEnd = false)
{
    /// <summary>The absolute channel path this tail follows (used for instance-name derivation + logging).</summary>
    public string Path { get; } = path;

    // When true, the FIRST attach to an existing file seeks to its current end instead of offset 0, so a
    // pre-existing append-only log (the native game log, which the watchdog opens with >> and never
    // rotates) is NOT replayed from the start — only lines appended after we attach are emitted. A later
    // rotation (inode change) still re-reads the fresh file from 0. Off for the container NDJSON channel
    // (the shim writes a fresh per-session file, so reading from 0 is exactly right).
    private readonly bool _primeAtEnd = primeAtEnd;
    private bool _primed;

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
    /// True when the <see cref="ReadNewLines"/> call just completed detected a <b>genuine new session</b>
    /// — an inode change after an already-established attach, or a same-inode shrink (the reuse safety
    /// net) — as opposed to this tail's very first-ever attach (there is nothing to reset: a fresh
    /// session-scoped consumer, e.g. <see cref="PlayerSessionMap"/>, starts empty anyway, so flagging the
    /// first attach as "reset" would be a no-op at best and a confusing signal at worst). A consumer that
    /// holds session-scoped state keys its reset off THIS flag rather than off inode-equality directly.
    /// Recomputed at the top of every <see cref="ReadNewLines"/> call.
    /// </summary>
    public bool LastReadResetSession { get; private set; }

    /// <summary>
    /// Read every complete line appended since the last call, advancing the offset. Returns an empty
    /// list when the file is absent, unreadable, or has no new complete line. Resets to offset 0 on an
    /// inode change (fresh container session) or a shrink (same-inode reuse safety net); either resets
    /// past the first attach are reported via <see cref="LastReadResetSession"/>.
    /// </summary>
    public IReadOnlyList<string> ReadNewLines()
    {
        LastReadResetSession = false;

        ulong? inode = TryReadInode(Path);
        if (inode is null)
        {
            // Absent / unreadable: forget where we were so a re-appearance is read from the start.
            ResetTo(null);
            return [];
        }

        // Fresh inode ⇒ new file. On the very FIRST attach with primeAtEnd, seek to EOF so an existing
        // append-only native log isn't replayed; every later inode change (rotation / new session) reads
        // the fresh file from 0. Without primeAtEnd this is the container path's "new session ⇒ read from
        // 0" (the primary rotation signal per the contract).
        if (_inode != inode)
        {
            bool alreadyAttachedOnce = _primed; // false only on this tail's very first attach
            if (_primeAtEnd && !_primed)
                PrimeAtEnd(inode);
            else
                ResetTo(inode);
            if (alreadyAttachedOnce)
                LastReadResetSession = true; // a real rotation, not the initial attach
        }
        _primed = true;

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
        // every new line. Also a genuinely new session, so it flags the same way as an inode change.
        if (length < _offset)
        {
            _offset = 0;
            _pending.Clear();
            LastReadResetSession = true;
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

    // First-attach seek-to-end: adopt the inode but start the offset at the current file length, so the
    // existing content of an append-only log is skipped and only subsequent appends are read. A failed
    // length read falls back to 0 (read from the start) rather than losing the file.
    private void PrimeAtEnd(ulong? inode)
    {
        _inode = inode;
        _pending.Clear();
        try { _offset = new FileInfo(Path).Length; }
        catch { _offset = 0; }
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
