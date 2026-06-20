using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The watchdog's <b>native role</b> for player presence: an always-on host-side ingester that tails each
/// NATIVE instance's own game log and emits player join/left wire events — the same events the container
/// path produces, so the contract (and every downstream consumer) is identical; only the mechanism
/// differs (containers self-report NDJSON via an in-image shim, native instances are matched here).
/// <para>
/// It is purely a file reader + event forwarder, and needs <b>no spawn-path change</b>: the watchdog's
/// <see cref="SpawnEngine"/> already execs the native game with its stdout+stderr appended to
/// <c>instance.LogFile</c> (a stable path), so detection is a tail of that file. The blueprint's
/// <c>player_joined_regex</c>/<c>player_left_regex</c> arrive on the <see cref="Instance"/> via the
/// kgsm-lib chokepoint (<see cref="IInstanceService.GetInstanceInfo"/>) — KGSM materialises them into the
/// instance config, which <c>instances info --json</c> emits. The match itself is the pure
/// <see cref="NativeLogMatcher"/> (the .NET analog of the in-image Perl shim).
/// </para>
/// <para>
/// Poll-driven (mirroring <see cref="PlayerPresenceIngester"/> and <see cref="CrashWatcher"/>). Each
/// instance's <see cref="EventChannelTail"/> uses <c>primeAtEnd</c>: the FIRST attach seeks to the log's
/// current end, so a long-running append-only native log is never replayed from the start (which would
/// flood stale joins) — only lines written after we attach are matched. Metadata (runtime, log path,
/// patterns) is static config, so it is fetched once per instance and cached; a non-native instance, an
/// instance with no patterns, or one with no log path is cached as "skip" and never re-fetched. Tailing a
/// <em>stopped</em> instance is harmless (no appends ⇒ no events; the parked tail catches the whole next
/// session when it appends), so this is decoupled from supervision/run-state, exactly like the container
/// ingester is decoupled from kgsm-lib enumeration.
/// </para>
/// </summary>
internal sealed class NativePlayerPresenceIngester(
    WatchdogOptions options,
    IInstanceService instances,
    IEventManagementService events,
    ILogger<NativePlayerPresenceIngester> logger) : BackgroundService
{
    private sealed record NativeWatch(EventChannelTail Tail, NativeLogMatcher Matcher);

    // name -> live watch (native + at least one valid pattern). Survives across ticks so the tail cursor
    // resumes.
    private readonly Dictionary<string, NativeWatch> _watches = new(StringComparer.Ordinal);

    // Names decided to be out of scope (container, no patterns, or no log path) — static config, so never
    // re-fetched. A transient fetch failure / not-yet-resolvable name is in NEITHER set, so it retries.
    private readonly HashSet<string> _skip = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(50, options.PlayerPresencePollMs));
        string root = ResolveInstancesDir();
        logger.LogInformation(
            "native player-presence ingester started; watching native instances under {Root} every {Ms}ms",
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
                    // Never let the loop die — it is an additive forwarder; a bad tick must not take the
                    // daemon down (supervision is its reason to live).
                    logger.LogError(ex, "native player-presence ingest pass threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        logger.LogInformation("native player-presence ingester stopped");
    }

    /// <summary>
    /// One scan + tail pass: discover instance names, resolve+cache each one's watch (or skip), then read
    /// new lines from every live watch, match, and emit. <c>internal</c> so a test can drive a single
    /// deterministic pass (and exercise resume + first-attach-at-EOF across passes).
    /// </summary>
    internal void IngestOnce(string root)
    {
        foreach (string name in DiscoverInstanceNames(root))
        {
            if (_skip.Contains(name))
                continue;

            if (!_watches.TryGetValue(name, out NativeWatch? watch))
            {
                watch = BuildWatch(name);
                if (watch is null)
                    continue; // skipped (recorded) or a transient miss (retry next tick)
                _watches[name] = watch;
            }

            foreach (string line in watch.Tail.ReadNewLines())
                HandleLine(name, line, watch.Matcher);
        }
    }

    /// <summary>
    /// Resolve one instance's metadata (via the kgsm-lib chokepoint) into a live watch, or record it as
    /// out-of-scope. Returns null when it was skipped OR when it could not be resolved yet (a transient
    /// kgsm error / a dir present before the instance is fully created) — the latter is deliberately NOT
    /// cached, so it retries on the next tick.
    /// </summary>
    private NativeWatch? BuildWatch(string name)
    {
        Instance? instance;
        try
        {
            instance = instances.GetInstanceInfo(name);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "native player-presence: could not read instance info for {Instance} (will retry)", name);
            return null;
        }

        if (instance is null)
            return null; // not resolvable yet — retry, don't skip permanently

        if (instance.Runtime != InstanceRuntime.Native)
        {
            _skip.Add(name); // containers are the other ingester's job
            return null;
        }

        var matcher = new NativeLogMatcher(instance.PlayerJoinedRegex, instance.PlayerLeftRegex);
        foreach (string warning in matcher.Warnings)
            logger.LogWarning("native player-presence for {Instance}: {Warning}", name, warning);

        if (!matcher.Enabled)
        {
            _skip.Add(name); // no (valid) patterns → detection disabled (honest unknown, no event)
            logger.LogDebug("native player-presence disabled for {Instance} (no valid patterns)", name);
            return null;
        }

        if (string.IsNullOrEmpty(instance.LogFile))
        {
            _skip.Add(name);
            logger.LogWarning("native player-presence for {Instance}: patterns set but log_file is empty — skipped", name);
            return null;
        }

        var tail = new EventChannelTail(instance.LogFile, primeAtEnd: true);
        logger.LogInformation(
            "native player-presence watching {Instance} (log {Log})", name, instance.LogFile);
        return new NativeWatch(tail, matcher);
    }

    private void HandleLine(string instanceName, string line, NativeLogMatcher matcher)
    {
        PlayerPresenceParser.ParseResult result = matcher.Match(line);
        if (!result.Emit)
        {
            // DropReason null = a normal non-matching line (the common case) — never logged. A non-null
            // reason is a real anomaly (matched-but-both-null, or a regex timeout) worth a warning.
            if (result.DropReason is not null)
                logger.LogWarning(
                    "dropping native presence line for {Instance} ({Reason}): {Line}",
                    instanceName, result.DropReason, Truncate(line));
            return;
        }

        Emit(result.EventName!, instanceName, result.PlayerId, result.PlayerName);
    }

    /// <summary>
    /// Emit one presence event through kgsm-lib, stamped <c>actor="system" / origin="system"</c> — an
    /// autonomous observation no human drove, identical to the container ingester and the supervisor's
    /// system/system emits. <b>Why not <c>actor:null</c>:</b> a null actor makes kgsm-lib omit
    /// <c>KGSM_EVENT_ACTOR</c> and kgsm's payload builder then falls back to the daemon's OS user — a
    /// fabricated human identity on an autonomous event. Null id/name pass as empty string (a string-based
    /// emit can't carry a literal JSON null mid-args; kgsm maps empty→null, and the matcher's
    /// at-least-one-non-null guard ensures at most one is empty). Best-effort: a failed emit is logged and
    /// swallowed, never crashes the ingester.
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
    /// Enumerate instance names from the two-level instances tree
    /// <c>&lt;root&gt;/&lt;blueprint&gt;/&lt;instance&gt;</c> (the <c>&lt;instance&gt;</c> dir is a symlink
    /// to the working dir; its name IS the kgsm instance name). Tolerant of a missing root / races — an
    /// unreadable level is skipped, never thrown. <c>internal</c> for tests.
    /// </summary>
    internal static IEnumerable<string> DiscoverInstanceNames(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (string blueprintDir in SafeEnumerateDirectories(root))
        {
            foreach (string instanceDir in SafeEnumerateDirectories(blueprintDir))
            {
                string name = Path.GetFileName(instanceDir);
                if (!string.IsNullOrEmpty(name))
                    yield return name;
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir); // follows the instance symlinks to real dirs
        }
        catch (Exception)
        {
            return []; // vanished / unreadable between ticks — skip this level
        }
    }

    /// <summary>
    /// The instances dir to watch — <c>KGSM_WATCHDOG_INSTANCES_DIR</c> if set, else
    /// <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm/instances</c>. Mirrors
    /// <see cref="PlayerPresenceIngester.ResolveInstancesDir"/> (the watchdog does not inherit KGSM's own
    /// <c>KGSM_INSTANCES_DIR</c>); resolved lazily here, after the user-drop in <c>CgroupBootstrap</c>.
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
