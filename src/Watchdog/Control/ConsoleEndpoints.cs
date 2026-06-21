using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Control;

/// <summary>
/// The console-stream surface (item #8): two GET endpoints over the same unix socket as the rest of
/// the control plane, both serving an instance's <b>stdout</b> as raw <c>text/plain</c>.
/// <list type="bullet">
/// <item><c>GET /console/{name}/follow</c> — live follow: streams every line appended to the log
/// <em>after</em> connect (chunked, until the client disconnects).</item>
/// <item><c>GET /console/{name}?tail=N</c> — finite scrollback: the last ≤N lines, then closes.</item>
/// </list>
/// <para>
/// <b>Native-only, by honesty.</b> The watchdog owns a native instance's stdout at spawn
/// (<see cref="SpawnEngine"/> appends it to <see cref="Instance.LogFile"/>); a container's stdout
/// belongs to Docker, so a non-native instance is <c>404</c> here — we do not invent a stream we
/// don't have. An unknown instance is likewise <c>404</c>; a native instance whose log file does not
/// exist yet (never started / stopped) is an honest <b>empty 200</b>, never a fabricated line.
/// </para>
/// <para>
/// <b>Raw text, no JSON.</b> stdout and stderr are merged at the source (<c>&gt;&gt; log 2&gt;&amp;1</c>),
/// so a per-line <c>{stream,ts}</c> envelope would fabricate structure that does not exist. The body is
/// the bytes the game wrote, newline-delimited — so <see cref="Model.WatchdogJsonContext"/> is untouched
/// and the surface stays AOT-clean.
/// </para>
/// </summary>
internal static class ConsoleEndpoints
{
    private const int DefaultTail = 200;
    private const int MaxTail = 5000;

    public static void MapConsole(this WebApplication app)
    {
        // Finite scrollback: the last <=N lines, then the response closes. `int?` (not `int`) so an
        // omitted ?tail binds to null → our default, rather than minimal-API returning 400.
        app.MapGet("/console/{name}", (string name, int? tail, IInstanceService instances) =>
        {
            string? log = ResolveNativeLogFile(instances, name);
            if (log is null)
                return Results.NotFound();

            int count = Math.Clamp(tail ?? DefaultTail, 0, MaxTail);
            IReadOnlyList<string> lines = ConsoleTailReader.ReadLastLines(log, count);

            // Honest empty (200) when the log doesn't exist yet or N=0 — never a 404, the instance IS
            // native and known; there is just nothing buffered to show.
            string body = lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";
            return Results.Text(body, "text/plain", System.Text.Encoding.UTF8);
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
    /// Resolve an instance's log file IFF it is a known, native instance — the gate both endpoints share.
    /// Returns null (→ 404) for an unknown instance, a non-native (container) instance, an empty
    /// <see cref="Instance.LogFile"/>, or a transient kgsm-lib read failure. A non-null, possibly
    /// not-yet-existing-on-disk, path means "native + known": absence of the file itself is handled
    /// downstream as honest-empty, not 404.
    /// </summary>
    private static string? ResolveNativeLogFile(IInstanceService instances, string name)
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

        return instance.LogFile;
    }
}
