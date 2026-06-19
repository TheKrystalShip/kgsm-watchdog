using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The watchdog's <b>container role</b>: an always-on host-side ingester that turns container
/// instances' self-reported player-presence into kgsm wire events. It is purely a file reader + event
/// forwarder — it <b>never shells <c>docker</c></b> and never supervises containers (Docker owns their
/// lifecycle); the watchdog's native-only supervision charter is untouched.
/// <para>
/// Each container instance bind-mounts a host directory <c>&lt;working_dir&gt;/events</c> to
/// <c>/run/kgsm</c> in the container, and the in-image detection shim appends NDJSON presence lines to
/// <c>events/events.ndjson</c> inside it. The ingester finds those channels by scanning the kgsm
/// instances dir — <c>&lt;instances&gt;/&lt;blueprint&gt;/&lt;instance&gt;/events/events.ndjson</c>, where
/// each <c>&lt;instance&gt;</c> entry is a symlink to the real working dir (so an arbitrary per-instance
/// install location is still reachable under one static root). The <c>&lt;instance&gt;</c> name is
/// derived from the path, decoupling the ingester from kgsm-lib enumeration.
/// </para>
/// <para>
/// Poll-driven (a <see cref="BackgroundService"/> + <see cref="PeriodicTimer"/>, mirroring
/// <see cref="CrashWatcher"/>) rather than a <c>FileSystemWatcher</c>: the (inode, offset) tail must
/// <c>statx</c> each tick anyway, and a poll handles "channel absent → appears later" and "a new
/// instance dir shows up" for free, with no inotify/AOT edge cases. Each line is parsed by the pure
/// <see cref="PlayerPresenceParser"/> and, on a valid join/left, emitted through kgsm-lib's string-based
/// <see cref="IEventManagementService.EmitWithProvenance"/> as <c>actor="system" / origin="system"</c> —
/// an autonomous observation no human drove (see <see cref="Emit"/> for why <c>"system"</c> not null). A
/// dropped line (malformed / unknown type / both-null id+name) is logged, never emitted.
/// </para>
/// </summary>
internal sealed class PlayerPresenceIngester(
    WatchdogOptions options,
    IEventManagementService events,
    ILogger<PlayerPresenceIngester> logger) : BackgroundService
{
    // One live tail per discovered channel path, keyed by absolute path. Survives across ticks so the
    // (inode, offset) cursor resumes; a channel that disappears keeps a (cheap, idle) tail that simply
    // returns nothing until/unless the path reappears, at which point the tail's own reset re-reads it.
    private readonly Dictionary<string, EventChannelTail> _tails = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, options.PlayerPresencePollMs));
        string root = ResolveInstancesDir();
        logger.LogInformation(
            "player-presence ingester started; watching {Root} for */*/events/events.ndjson every {Ms}ms",
            root, interval.TotalMilliseconds);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    IngestOnce(root);
                }
                catch (Exception ex)
                {
                    // Never let the ingest loop die — it is an additive forwarder; a bad tick must not
                    // take the whole daemon down (the supervision loop is the daemon's reason to live).
                    logger.LogError(ex, "player-presence ingest pass threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        logger.LogInformation("player-presence ingester stopped");
    }

    /// <summary>
    /// One scan + tail pass: discover channels, read new lines from each, parse and emit. <c>internal</c>
    /// so a test can drive a single deterministic pass (and exercise resume across passes) without
    /// racing the <see cref="PeriodicTimer"/> loop.
    /// </summary>
    internal void IngestOnce(string root)
    {
        foreach (string channelPath in DiscoverChannels(root))
        {
            if (!_tails.TryGetValue(channelPath, out var tail))
            {
                tail = new EventChannelTail(channelPath);
                _tails[channelPath] = tail;
            }

            string instanceName = DeriveInstanceName(channelPath);
            foreach (string line in tail.ReadNewLines())
                HandleLine(instanceName, channelPath, line);
        }
    }

    /// <summary>
    /// Enumerate <c>&lt;root&gt;/&lt;blueprint&gt;/&lt;instance&gt;/events/events.ndjson</c> — a manual,
    /// two-level walk + <c>File.Exists</c> rather than a glob, so symlink traversal is explicit and not
    /// at the mercy of a glob library's symlink semantics. Tolerant of a missing root / races: an
    /// unreadable level is skipped, never thrown.
    /// </summary>
    internal static IEnumerable<string> DiscoverChannels(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (string blueprintDir in SafeEnumerateDirectories(root))
        {
            foreach (string instanceDir in SafeEnumerateDirectories(blueprintDir))
            {
                // Path.Combine, not the matched dir-enumeration result, so the events file path is built
                // deterministically (and traverses the instance→working_dir symlink when statted/read).
                string channel = Path.Combine(instanceDir, "events", "events.ndjson");
                if (File.Exists(channel))
                    yield return channel;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try
        {
            // EnumerateDirectories follows the instance symlinks (they resolve to real dirs), so a
            // blueprint dir's instance-symlinks are walked transparently.
            return Directory.EnumerateDirectories(dir);
        }
        catch (Exception)
        {
            return []; // vanished / unreadable between ticks — skip this level
        }
    }

    /// <summary>
    /// Derive <c>&lt;instance_name&gt;</c> from a channel path
    /// <c>.../&lt;blueprint&gt;/&lt;instance&gt;/events/events.ndjson</c>: it is the directory two levels
    /// above the file (the dir that contains <c>events/</c>). Taken from the matched (symlink-form) path,
    /// not a readlink-resolved one — the kgsm instance name is carried by the instances-dir path by
    /// construction (<c>working_dir = .../&lt;blueprint&gt;/&lt;instance&gt;</c>, same final segment).
    /// </summary>
    internal static string DeriveInstanceName(string channelPath)
    {
        // events.ndjson -> events/ -> <instance>/
        string? eventsDir = Path.GetDirectoryName(channelPath);          // .../<instance>/events
        string? instanceDir = Path.GetDirectoryName(eventsDir);          // .../<instance>
        return Path.GetFileName(instanceDir) is { Length: > 0 } name ? name : "unknown";
    }

    private void HandleLine(string instanceName, string channelPath, string line)
    {
        var result = PlayerPresenceParser.Parse(line);
        if (!result.Emit)
        {
            // A blank line is the normal trailing-newline case — don't spam the log for it.
            if (result.DropReason != "blank line")
                logger.LogWarning(
                    "dropping presence line for {Instance} ({Reason}): {Line}",
                    instanceName, result.DropReason, Truncate(line));
            return;
        }

        Emit(result.EventName!, instanceName, result.PlayerId, result.PlayerName);
    }

    /// <summary>
    /// Emit one presence event through kgsm-lib, stamped <c>actor="system" / origin="system"</c> — an
    /// autonomous observation no human drove, mirroring the supervisor's existing system/system emits
    /// (<c>instance-crashed</c>/<c>-restarted</c>/<c>-started</c>). <b>Why not <c>actor:null</c>:</b> a
    /// null actor makes kgsm-lib omit <c>KGSM_EVENT_ACTOR</c>, and kgsm's payload builder then falls back
    /// to the daemon's OS user — a <em>fabricated</em> human identity on an autonomous event, violating
    /// the never-fabricate-provenance doctrine. Passing <c>"system"</c> explicitly suppresses that
    /// fallback (frozen-contract §A/§3, corrected 2026-06-19).
    /// <para>
    /// The positional params are <c>instance, player_id, player_name</c>; a null id/name is passed as an
    /// empty string (a string-based emit cannot carry a literal JSON null in a middle positional arg, and
    /// the at-least-one-non-null guard upstream guarantees at most one is empty — kgsm maps empty→null in
    /// the payload, as it already does for origin). Best-effort: a failed emit is logged and swallowed — a
    /// dropped event is the same honest "no backfill" boundary the downstream consumer already accepts,
    /// and must never crash the ingester.
    /// </para>
    /// </summary>
    private void Emit(string eventName, string instanceName, string? playerId, string? playerName)
    {
        try
        {
            events.EmitWithProvenance(
                eventName,
                actor: "system",
                origin: "system",
                instanceName, playerId ?? string.Empty, playerName ?? string.Empty);

            logger.LogInformation(
                "emitted {Event} for {Instance} (id={Id} name={Name})",
                eventName, instanceName,
                string.IsNullOrEmpty(playerId) ? "<none>" : playerId,
                string.IsNullOrEmpty(playerName) ? "<none>" : playerName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "failed to emit {Event} for {Instance} (event dropped)", eventName, instanceName);
        }
    }

    /// <summary>
    /// The instances dir to watch: the explicit <c>KGSM_WATCHDOG_INSTANCES_DIR</c> if set, else
    /// <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm/instances</c>. Resolved lazily here (the ingester
    /// starts after <c>CgroupBootstrap</c>, so <c>HOME</c> is the dropped KGSM user's) — the watchdog
    /// does not inherit KGSM's own <c>KGSM_INSTANCES_DIR</c>. Mirrors <c>DesiredStateStore.ResolvePath</c>.
    /// </summary>
    internal string ResolveInstancesDir()
    {
        if (!string.IsNullOrEmpty(options.InstancesDir))
            return options.InstancesDir;

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/var/lib", ".local", "share");

        return Path.Combine(dataHome, "kgsm", "instances");
    }

    private static string Truncate(string s, int max = 200)
        => s.Length <= max ? s : s[..max] + "…";
}
