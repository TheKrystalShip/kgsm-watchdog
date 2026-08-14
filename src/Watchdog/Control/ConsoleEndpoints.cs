using System.Globalization;

using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Control;

/// <summary>
/// The console-stream surface: GET endpoints over the same unix socket as the rest of the control
/// plane, serving an instance's <b>stdout</b> as raw <c>text/plain</c>.
/// <list type="bullet">
/// <item><c>GET /console/{name}/follow</c> — live follow: streams every line appended to the log
/// <em>after</em> connect (chunked, until the client disconnects).</item>
/// <item><c>GET /console/{name}?tail=N&amp;run=I</c> — finite scrollback: the last ≤N lines of run
/// <c>I</c>, then closes. <c>run</c> defaults to 0, the run in progress.</item>
/// <item><c>GET /console/{name}/runs</c> — the run index: which stretches of console exist, newest
/// first, and when each ended.</item>
/// </list>
/// <para>
/// <b>A console has runs, and they are addressed one at a time.</b> The log rotates on every fresh
/// spawn (<see cref="SpawnEngine.RotateLogFile"/>), so a crash and its restart leave the cause in the
/// previous run while the live log holds a clean boot. Those are two processes, so they are two
/// responses: this surface will not splice them into one body, because undelimited text reads as one
/// continuous stream and invites a caller to narrate a stack trace and the boot that followed it as a
/// single timeline. A caller that wants the crash asks <c>/runs</c> which run ended when, and reads
/// that one.
/// </para>
/// <para>
/// <b>Native-only, by honesty.</b> The watchdog owns a native instance's stdout at spawn
/// (<see cref="SpawnEngine"/> appends it to <see cref="Instance.LogFile"/>); a container's stdout
/// belongs to Docker, so a non-native instance is <c>404</c> here — we do not invent a stream we
/// don't have. An unknown instance is likewise <c>404</c>; a native instance whose log file does not
/// exist yet (never started / stopped) is an honest <b>empty 200</b>, never a fabricated line.
/// </para>
/// <para>
/// <b>Raw text for output, JSON only for the index.</b> stdout and stderr are merged at the source
/// (<c>&gt;&gt; log 2&gt;&amp;1</c>), so a per-line <c>{stream,ts}</c> envelope would fabricate structure
/// that does not exist; a console body is the bytes the game wrote, newline-delimited. The run index
/// is a different kind of thing — measured metadata about files, structured because it genuinely is —
/// so it serializes through <see cref="Model.WatchdogJsonContext"/> and the surface stays AOT-clean.
/// </para>
/// </summary>
internal static class ConsoleEndpoints
{
    private const int DefaultTail = 200;

    /// <summary>
    /// The most lines ONE request will assemble. It bounds a single response, not how far back a
    /// caller can read: the window is addressed by byte offset, so paging with the
    /// <see cref="StartHeader"/> cursor reaches the beginning of a run of any size. The whole file in
    /// one piece is <c>/console/{name}/download</c>, which streams and holds none of it.
    /// </summary>
    private const int MaxTail = 5000;

    /// <summary>Where in the file the window served begins — the cursor to page further back with.</summary>
    private const string StartHeader = "X-Console-Start";

    /// <summary>Where it ends (exclusive): the caller's own <c>?end=</c>, or the length at read time.</summary>
    private const string EndHeader = "X-Console-End";

    /// <summary>Copy buffer for the download stream — one per request, not a buffer per megabyte.</summary>
    private const int BlockSize = 64 * 1024;

    /// <summary>
    /// Copy exactly <paramref name="length"/> bytes, which is what keeps the body honest against the
    /// Content-Length declared for it while the game appends to the far end of the same file.
    /// </summary>
    private static async Task CopyExactAsync(Stream from, Stream to, long length, CancellationToken ct)
    {
        byte[] buffer = new byte[BlockSize];
        long left = length;
        while (left > 0)
        {
            int want = (int)Math.Min(buffer.Length, left);
            int got = await from.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (got <= 0)
                break;   // truncated under us (rotation) — stop at what was actually there
            await to.WriteAsync(buffer.AsMemory(0, got), ct).ConfigureAwait(false);
            left -= got;
        }
    }

    public static void MapConsole(this WebApplication app)
    {
        // Finite scrollback: the last <=N lines of ONE run, then the response closes. `int?` (not
        // `int`) so an omitted ?tail / ?run binds to null → our default, rather than minimal-API
        // returning 400.
        //
        // `?end=OFFSET` reads the window ending at that byte offset instead of at the end of the file,
        // and every response carries the range it served in X-Console-Start / X-Console-End. That is
        // what makes reading further back exact: the caller asks for the window ending at the Start it
        // was last given, so a game appending throughout cannot shift the answer, and a Start of 0
        // says the run begins there. A line count from the end would name a different line on every
        // request and quietly overlap or skip.
        app.MapGet("/console/{name}", (string name, int? tail, int? run, long? end, HttpContext http, IInstanceService instances) =>
        {
            Instance? instance = ResolveNativeInstance(instances, name);
            if (instance is null)
                return Results.NotFound();

            int index = run ?? CurrentRun;
            if (index < 0)
                return Results.NotFound();

            IReadOnlyList<RunFile> runs = EnumerateRuns(instance);
            if (index >= runs.Count)
                // An instance that has never produced output has no run 0 — that is the honest-empty
                // case the surface has always returned, not a missing resource. A run the caller
                // named explicitly and that does not exist IS missing.
                return index == CurrentRun
                    ? Results.Text(string.Empty, "text/plain", System.Text.Encoding.UTF8)
                    : Results.NotFound();

            int count = Math.Clamp(tail ?? DefaultTail, 0, MaxTail);
            ConsoleWindow window = ConsoleTailReader.ReadWindow(runs[index].File.FullName, count, end ?? -1);

            http.Response.Headers[StartHeader] = window.Start.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers[EndHeader] = window.End.ToString(CultureInfo.InvariantCulture);

            // Honest empty (200) when the log doesn't exist yet or N=0 — never a 404, the instance IS
            // native and known; there is just nothing buffered to show.
            string body = window.Lines.Count == 0 ? string.Empty : string.Join('\n', window.Lines) + "\n";
            return Results.Text(body, "text/plain", System.Text.Encoding.UTF8);
        });

        // The whole of ONE run, streamed. The paged read above answers "show me more" a window at a
        // time; this answers "give me the file" — the thing somebody attaches to a bug report — and it
        // is a separate route because it is a different shape: no line budget, and the body is copied
        // block by block rather than assembled in memory, so a multi-gigabyte log costs the daemon a
        // buffer rather than its whole RSS.
        app.MapGet("/console/{name}/download", async (
            string name, int? run, HttpContext http, IInstanceService instances) =>
        {
            Instance? instance = ResolveNativeInstance(instances, name);
            if (instance is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            int index = run ?? CurrentRun;
            IReadOnlyList<RunFile> runs = EnumerateRuns(instance);
            if (index < 0 || (index >= runs.Count && index != CurrentRun))
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            http.Response.ContentType = "text/plain; charset=utf-8";
            if (index >= runs.Count)
            {
                http.Response.ContentLength = 0;   // known native instance that has never printed
                return;
            }

            FileStream file;
            try
            {
                file = new FileStream(runs[index].File.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, BlockSize, useAsync: true);
            }
            catch (IOException)
            {
                http.Response.ContentLength = 0;   // vanished or unreadable between listing and open
                return;
            }

            await using (file)
            {
                // The length is snapshotted at open and exactly that many bytes are copied: the game
                // is still appending, and a body longer than the Content-Length announced for it is a
                // broken response, not a fresher one.
                long length = file.Length;
                http.Response.ContentLength = length;
                await CopyExactAsync(file, http.Response.Body, length, http.RequestAborted).ConfigureAwait(false);
            }
        });

        // The run index: what stretches of console exist and when each one ended. A caller correlating
        // an event against output (which run was live when this crashed?) reads this, matches on
        // EndedAt, and asks for that Index — it never has to reason about rotation or guess a path.
        app.MapGet("/console/{name}/runs", (
            string name, IInstanceService instances, InstanceSupervisor sup, RunHistoryStore history) =>
        {
            Instance? instance = ResolveNativeInstance(instances, name);
            if (instance is null)
                return Results.NotFound();

            // Whether a process is alive in the instance's cgroup right now. Holding the live log is
            // not the same as being the run in progress: an instance that stopped — or crashed with
            // no restart behind it — keeps its last log at that path until the next spawn rotates it,
            // and that run is over.
            bool inProgress = sup.IsRunning(name);
            IReadOnlyList<RunRecord> ledger = history.RunsFor(name);

            ConsoleRun[] runs = EnumerateRuns(instance)
                .Select((r, i) =>
                {
                    bool current = r.Current && inProgress;
                    DateTime lastOutput = r.File.LastWriteTimeUtc;
                    RunRecord? record = current ? null : MatchLedger(ledger, lastOutput);

                    return new ConsoleRun(
                        Index: i,
                        Current: current,
                        // A run in progress has not ended; every other run has, and its last write is
                        // when it stopped printing. The same instant is therefore reported under two
                        // different names — an ending only where one was actually observed.
                        EndedAt: current ? null : lastOutput,
                        LastOutputAt: lastOutput,
                        SizeBytes: r.File.Length,
                        Outcome: current ? RunningOutcome : record?.Outcome ?? UnknownOutcome,
                        ExitCode: record?.ExitCode);
                })
                .ToArray();

            // An empty array is a real answer: a known native instance that has never produced output.
            return Results.Json(runs, WatchdogJsonContext.Default.ConsoleRunArray);
        });

        // Live follow: stream lines appended after connect until the client disconnects. Mirrors the
        // native player-presence ingester's primeAtEnd tail + PeriodicTimer loop.
        app.MapGet("/console/{name}/follow", async (
            string name, HttpContext http, IInstanceService instances, WatchdogOptions options) =>
        {
            string? log = ResolveNativeLogFile(instances, name);
            if (log is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            http.Response.ContentType = "text/plain; charset=utf-8";

            CancellationToken ct = http.RequestAborted;
            var tail = new EventChannelTail(log, primeAtEnd: true); // only lines appended after connect
            var interval = TimeSpan.FromMilliseconds(Math.Max(50, options.ConsolePollMs));
            using var timer = new PeriodicTimer(interval);

            try
            {
                // Prime once so a fast first append isn't missed before the first tick, then poll.
                do
                {
                    foreach (string line in tail.ReadNewLines())
                        await http.Response.WriteAsync(line + "\n", ct).ConfigureAwait(false);
                    await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                // Client disconnected (RequestAborted) — stop the loop, no leak. Normal, not an error.
            }
        });
    }

    /// <summary>
    /// Resolve an instance's log file IFF it is a known, native instance — the gate every endpoint
    /// shares. Returns null (→ 404) for an unknown instance, a non-native (container) instance, an
    /// empty <see cref="Instance.LogFile"/>, or a transient kgsm-lib read failure. A non-null,
    /// possibly not-yet-existing-on-disk, path means "native + known": absence of the file itself is
    /// handled downstream as honest-empty, not 404.
    /// </summary>
    private static string? ResolveNativeLogFile(IInstanceService instances, string name) =>
        ResolveNativeInstance(instances, name)?.LogFile;

    /// <summary>The same gate, keeping the whole instance — the run index needs its logs directory too.</summary>
    private static Instance? ResolveNativeInstance(IInstanceService instances, string name)
    {
        Instance? instance;
        try
        {
            instance = instances.GetInstanceInfo(name);
        }
        catch (Exception)
        {
            return null; // can't resolve right now — 404 rather than a fabricated stream
        }

        if (instance is null || instance.Runtime != InstanceRuntime.Native || string.IsNullOrEmpty(instance.LogFile))
            return null;

        return instance;
    }

    /// <summary>Index 0: the run in progress, whenever the instance has produced any output at all.</summary>
    private const int CurrentRun = 0;

    /// <summary>The run in progress has not ended, so it has no ending to report.</summary>
    private const string RunningOutcome = "running";

    /// <summary>
    /// No ledger row joins to this run — it predates the ledger, its console could not be read when it
    /// ended, or the daemon was down at the time. An absence of knowledge, never a clean ending.
    /// </summary>
    private const string UnknownOutcome = "unknown";

    /// <summary>
    /// How far a ledger row's end time may sit from a console file's last write and still describe it.
    /// <para>
    /// Both values originate from the same <c>stat</c> — the supervisor reads the file's mtime when it
    /// concludes the run, and rotation moves the file with <c>rename(2)</c>, which leaves mtime alone —
    /// so an exact match is the expected case. The tolerance covers filesystems that store a coarser
    /// timestamp than the one .NET hands back, and nothing more: it is far below the gap between two
    /// runs even in a crash loop, so it can never let one run's row describe another's console.
    /// </para>
    /// </summary>
    private static readonly TimeSpan LedgerJoinTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The ledger row describing the run that stopped printing at <paramref name="endedAt"/>, or null
    /// when none does. Nearest wins, so an instance that crash-looped through several runs a second
    /// apart still resolves each console to its own row.
    /// </summary>
    internal static RunRecord? MatchLedger(IReadOnlyList<RunRecord> ledger, DateTime endedAt)
    {
        RunRecord? best = null;
        TimeSpan closest = TimeSpan.MaxValue;

        foreach (RunRecord row in ledger)
        {
            TimeSpan gap = (row.EndedAt - endedAt).Duration();
            if (gap > LedgerJoinTolerance || gap >= closest)
                continue;

            best = row;
            closest = gap;
        }

        return best;
    }

    /// <summary>One run's backing file, and whether it is the live one.</summary>
    internal readonly record struct RunFile(FileInfo File, bool Current);

    /// <summary>
    /// The instance's runs, newest first: the live log (when it exists on disk) followed by every
    /// rotated sibling in the logs directory. A run with no file is not listed — an instance that has
    /// never started has no runs, which is a real answer rather than an empty placeholder.
    /// <para>
    /// <b>Ordered by last-write time, not by filename.</b> The last write is the last line the run
    /// printed, so it is the measured end of that run; the name only labels it, and a log rotated by
    /// an older build is labelled with the rotation moment instead — days off for an instance that
    /// sat stopped. Sorting on the filesystem keeps even those files in true order.
    /// </para>
    /// <para>
    /// Enumeration is best-effort: a logs directory that cannot be read yields the live run alone
    /// rather than failing the request, because a partial history is worth more than none and the
    /// caller can see what it got.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<RunFile> EnumerateRuns(Instance instance)
    {
        var runs = new List<RunFile>();

        var live = new FileInfo(instance.LogFile);
        if (live.Exists)
            runs.Add(new RunFile(live, Current: true));

        // Mirrors SpawnEngine.RotateLogFile's destination, including its empty-logs-dir fallback to
        // the log file's own directory, so the reader looks exactly where the writer puts them.
        string dir = !string.IsNullOrEmpty(instance.LogsDir) ? instance.LogsDir : (live.DirectoryName ?? "");
        if (dir.Length == 0)
            return runs;

        try
        {
            string stem = Path.GetFileNameWithoutExtension(instance.LogFile);
            string ext = Path.GetExtension(instance.LogFile);
            IEnumerable<FileInfo> rotated = new DirectoryInfo(dir)
                .EnumerateFiles($"{stem}.*{ext}")
                // Guards the fallback case above, where the live log shares the directory it rotates
                // into: a run must never appear twice under two indices.
                .Where(f => !string.Equals(f.FullName, live.FullName, StringComparison.Ordinal))
                .OrderByDescending(f => f.LastWriteTimeUtc);

            foreach (FileInfo f in rotated)
                runs.Add(new RunFile(f, Current: false));
        }
        catch (Exception)
        {
            // Unreadable/removed logs directory — return what we have rather than failing the read.
        }

        return runs;
    }
}
