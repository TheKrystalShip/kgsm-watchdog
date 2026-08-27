using System.Globalization;
using TheKrystalShip.KGSM.Watchdog.Interop;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// When a process started, taken from the kernel's own record of it in <c>/proc</c>.
/// <para>
/// Two questions need this and neither can be answered out of the daemon's memory. A run the daemon
/// <em>adopted</em> is older than the daemon supervising it, so its age exists nowhere but the kernel.
/// And a run needs a name that outlives the daemon: <see cref="RunKeyFor"/> pairs the leader's pid with
/// the tick it started on, which together identify exactly one run — a pid on its own is recycled, and
/// a start tick on its own is shared by everything that started in the same hundredth of a second.
/// </para>
/// <para>
/// Every read is best-effort and answers null rather than a guess. A process that exits mid-read, a
/// <c>stat</c> line that does not parse, a C library that does not know the tick rate: each leaves the
/// caller with an honest unknown, which callers report as "unknown" and never as a substituted value.
/// </para>
/// </summary>
internal static class ProcessStartClock
{
    /// <summary>
    /// Index of <c>starttime</c> among the whitespace-separated fields that follow the comm field's
    /// closing parenthesis. <c>starttime</c> is field 22 of <c>/proc/&lt;pid&gt;/stat</c> and the first
    /// field after that parenthesis is field 3, so it sits at 22 - 3.
    /// </summary>
    private const int StartTimeFieldOffset = 19;

    /// <summary>
    /// Clock ticks per second, asked of the C library once. Zero or negative means the value could not
    /// be read, which propagates as an unknown start time rather than as a conventional 100.
    /// </summary>
    private static readonly Lazy<long> TicksPerSecond = new(
        () => NativeMethods.sysconf(NativeMethods._SC_CLK_TCK),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The instant this kernel booted, from <c>/proc/stat</c>'s <c>btime</c>, read once. It is the
    /// origin every process's <c>starttime</c> is measured from, and it does not change while the
    /// kernel is up — so reading it once also keeps two calls from disagreeing by a second when the
    /// kernel re-derives it after a clock adjustment.
    /// </summary>
    private static readonly Lazy<DateTime?> BootedAtUtc = new(
        ReadBootTime, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The name of the run <paramref name="pid"/> belongs to — its pid and the kernel tick it started
    /// on — or null when the process cannot be read. Two different runs never share one, and one run
    /// keeps the same key for as long as it lives, on this daemon and on every daemon after it.
    /// </summary>
    public static string? RunKeyFor(int pid) =>
        ReadStartTicks(pid) is long ticks
            ? string.Create(CultureInfo.InvariantCulture, $"{pid}:{ticks}")
            : null;

    /// <summary>
    /// When <paramref name="pid"/> started, in UTC, or null when the process, the boot time or the tick
    /// rate cannot be read.
    /// </summary>
    public static DateTime? StartedAtUtc(int pid)
    {
        if (ReadStartTicks(pid) is not long ticks)
            return null;
        if (BootedAtUtc.Value is not DateTime booted)
            return null;

        long tickRate = TicksPerSecond.Value;
        if (tickRate <= 0)
            return null;

        return booted.AddSeconds((double)ticks / tickRate);
    }

    /// <summary>
    /// <c>starttime</c> for <paramref name="pid"/>: how many clock ticks after boot it began.
    /// </summary>
    /// <remarks>
    /// The fields are parsed from the last <c>)</c> rather than by splitting the whole line, because the
    /// second field is the executable's name in parentheses and a game binary is free to contain spaces
    /// and parentheses of its own. Everything after the final <c>)</c> is fixed-shape, space-separated
    /// numbers and single letters.
    /// </remarks>
    private static long? ReadStartTicks(int pid)
    {
        if (pid <= 0)
            return null;

        string text;
        try
        {
            text = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
        }
        catch
        {
            return null; // exited, or not ours to read -> unknown
        }

        int comm = text.LastIndexOf(')');
        if (comm < 0)
            return null;

        string[] fields = text[(comm + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > StartTimeFieldOffset
            && long.TryParse(fields[StartTimeFieldOffset], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)
                ? ticks
                : null;
    }

    /// <summary><c>btime</c> from <c>/proc/stat</c> — unix seconds — as UTC, or null if unreadable.</summary>
    private static DateTime? ReadBootTime()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/stat"))
            {
                if (!line.StartsWith("btime ", StringComparison.Ordinal))
                    continue;

                return long.TryParse(line.AsSpan(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
                    ? DateTime.UnixEpoch.AddSeconds(seconds)
                    : null;
            }
        }
        catch
        {
            // unreadable -> unknown
        }
        return null;
    }
}
