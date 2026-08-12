using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Watchdog.Events;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Exceptions;
using TheKrystalShip.KGSM.Services;
using TheKrystalShip.KGSM.Watchdog.Cgroup;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// A poll-based player-presence detector for game servers that support Source RCON.
/// For each native instance with RCON configured (<c>rcon_port</c> non-null,
/// <c>rcon_password</c> non-empty), it periodically connects via RCON, executes the
/// configured players command, diffs the result against the previous poll, and emits
/// <c>instance-player-joined</c> / <c>instance-player-left</c> wire events — the same
/// events the log-based <see cref="NativePlayerPresenceIngester"/> produces.
/// </summary>
/// <remarks>
/// <b>Coexistence with log tail.</b> Both this poller and the native log ingester write
/// to the shared <see cref="PlayerSessionStore"/>. The store's dedup logic
/// (insert-if-absent on join, resolve-and-evict on leave) prevents double-counting when
/// a game has BOTH log-based join detection AND RCON-based leave detection. The session
/// map correlates the two: the log tail detects the join, this poller detects the leave.
///
/// <b>Connection lifecycle.</b> Each poll cycle creates a fresh TCP connection, authenticates,
/// executes the command, and disconnects. This is simpler and more resilient than a
/// persistent connection (handles server restarts, network blips, etc.) at the cost of
/// reconnect overhead every N seconds (trivial for RCON).
///
/// <b>Error handling.</b> RCON connection failures are logged as warnings and skipped —
/// no event is ever fabricated. The poller retries on the next tick.
///
/// <b>State tracking.</b> The poller maintains its own per-instance previous-poll state
/// (player IDs seen in the last successful poll). This is separate from the
/// <see cref="PlayerSessionStore"/> session map, which handles cross-mechanism correlation.
///
/// <b>Instance metadata caching.</b> Instance info (RCON config, etc.) is cached
/// per instance and only re-fetched every 60 seconds. This avoids shelling out to
/// <c>kgsm instances info --json</c> on every 1-second tick for every instance.
/// Instance liveness is checked via the supervisor's own state (<see cref="InstanceSupervisor.Status"/>),
/// not the PID file.
/// </remarks>
internal sealed class RconPlayerPresencePoller(
    IInstanceService instances,
    WatchdogJournal journal,
    PlayerSessionStore sessionStore,
    InstanceSupervisor supervisor,
    ILogger<RconPlayerPresencePoller> logger) : BackgroundService
{
    private const string EventJoined = "instance-player-joined";
    private const string EventLeft = "instance-player-left";

    private static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(5);
    private const int MetadataCacheSeconds = 60;

    /// <summary>
    /// Per-instance tracking of the last poll's player set. Keyed by instance name; each value holds
    /// the previous successful poll's entries, keyed the way an entry identifies itself between polls
    /// (<see cref="RconPlayerResponseParser.PlayerEntry.Key"/>). The whole entry is kept rather than
    /// the key alone, so a leave reports the id the server actually stated and does not present the
    /// key as one when the game's list carries only names.
    /// </summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, RconPlayerResponseParser.PlayerEntry>> _previousPollState = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-instance last-poll timestamp (Environment.TickCount64, milliseconds).
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _lastPollTimestamps = new(StringComparer.Ordinal);

    /// <summary>
    /// Cached instance metadata. Keyed by instance name. Only instances that have
    /// RCON configured (non-null port, non-empty password) are cached here.
    /// Null value means "checked, no RCON — skip".
    /// </summary>
    private readonly ConcurrentDictionary<string, Instance?> _instanceCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-instance timestamp of when the metadata was last refreshed.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _metadataCacheTimestamps = new(StringComparer.Ordinal);

    /// <summary>
    /// Per-instance signature of the most recent poll failure, held so a persistent one is logged at
    /// warning once and at debug thereafter. Absent means the last poll succeeded.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _lastFailureSignature = new(StringComparer.Ordinal);

    /// <summary>
    /// Instances already complained about for a blueprint-level problem, so it is said once.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _warned = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("RCON player-presence poller started");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await PollOnce(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "RCON player-presence poll pass threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        logger.LogInformation("RCON player-presence poller stopped");
    }

    /// <summary>
    /// One poll pass: discover instances with RCON configured, poll each one, diff, emit.
    /// </summary>
    internal async Task PollOnce(CancellationToken cancellationToken)
    {
        string root = ResolveInstancesDir();
        long now = Environment.TickCount64;

        foreach (string name in DiscoverInstanceNames(root))
        {
            // Check cache: do we have a recent metadata fetch for this instance?
            bool cacheValid = _instanceCache.TryGetValue(name, out var cachedInstance)
                && _metadataCacheTimestamps.TryGetValue(name, out long cachedAt)
                && (now - cachedAt) < MetadataCacheSeconds * 1000L;

            if (!cacheValid)
            {
                // Fetch (or re-fetch) instance metadata
                Instance? instance;
                try
                {
                    instance = instances.GetInstanceInfo(name);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "RCON poller: could not read instance info for {Instance} (will retry)", name);
                    _instanceCache.TryRemove(name, out _);
                    continue;
                }

                if (instance is null)
                {
                    _instanceCache[name] = null; // cache the miss
                    _metadataCacheTimestamps[name] = now;
                    continue;
                }

                // The verdict is PlayerDetection's, not this loop's. GET /players reports what this
                // daemon can observe, and an instance polled here while the endpoint calls it
                // undetectable would tell a consumer "nobody can tell" about a roster being actively
                // read. One predicate, so the two cannot say different things.
                if (!PlayerDetection.FromRcon(instance))
                {
                    // RCON wired but unreadable is the skip an operator needs to see: without a
                    // command there is nothing to ask, and without a compiling response pattern the
                    // reply cannot be parsed — either way the poll could only produce a roster that is
                    // empty for want of parsing, which is indistinguishable from nobody connected.
                    // An instance with no RCON at all is not a misconfiguration and says nothing.
                    if (instance.Runtime == InstanceRuntime.Native
                        && instance.RconPort is not null
                        && !string.IsNullOrEmpty(instance.RconPassword))
                    {
                        WarnOnce(name, string.IsNullOrWhiteSpace(instance.RconPlayersCommand)
                            ? "RCON is configured for {Instance} but its blueprint names no rcon_players_command, so there is nothing to ask for its player list — RCON presence is off for this instance"
                            : string.IsNullOrWhiteSpace(instance.RconPlayersRegex)
                            ? "RCON is configured for {Instance} but its blueprint sets no rcon_players_regex, so its player list cannot be read — RCON presence is off for this instance"
                            : "RCON is configured for {Instance} but its blueprint's rcon_players_regex does not compile, so its player list cannot be read — RCON presence is off for this instance",
                            name);
                    }

                    _instanceCache[name] = null;
                    _metadataCacheTimestamps[name] = now;
                    continue;
                }

                _instanceCache[name] = instance;
                _metadataCacheTimestamps[name] = now;
                cachedInstance = instance;
            }

            // At this point cachedInstance is non-null (has RCON configured)
            if (cachedInstance is null)
                continue;

            // Check via the supervisor's authoritative state, not the PID file.
            // Desired != "running" means the operator stopped it; !Populated means
            // the cgroup is empty (no process). Either way, RCON will return nothing.
            var state = supervisor.Status(name);
            if (state is null || state.Desired != "running" || !state.Populated)
            {
                // Not running: drop the last poll's player set. It describes a process that no longer
                // exists, and carrying it into the next run turns the first poll after a restart into a
                // burst of "left" events for players who disconnected with the previous session — after
                // the supervisor already cleared the map those events resolve against.
                _previousPollState.TryRemove(name, out _);

                // A fresh run deserves a fresh warning: whatever RCON was refusing last time is a
                // property of a process that no longer exists.
                _lastFailureSignature.TryRemove(name, out _);
                continue;
            }

            int intervalSeconds = cachedInstance.RconPollIntervalSeconds is > 0
                ? cachedInstance.RconPollIntervalSeconds.Value
                : 10;
            if (intervalSeconds < (int)MinPollInterval.TotalSeconds)
                intervalSeconds = (int)MinPollInterval.TotalSeconds;

            if (!ShouldPoll(name, intervalSeconds))
                continue;

            await PollInstance(cachedInstance, cachedInstance.RconPort!.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollInstance(Instance instance, int port, CancellationToken cancellationToken)
    {
        string name = instance.Name;

        // Get previous state for this instance. With none — the first poll of a run, or the first after a
        // hot-swap, which discards this dictionary while the session map is carried across — seed it from
        // the session map rather than starting empty. Starting empty makes the first poll blind to leaves:
        // a player who disconnects before it runs is absent from both sides of the diff, so nothing retires
        // the session the map still holds for them.
        _previousPollState.TryGetValue(name, out var previousPlayers);
        previousPlayers ??= SeedFromSessionMap(name);

        try
        {
            await using var rcon = new RconClient();
            await rcon.ConnectAsync("127.0.0.1", port, instance.RconPassword, cancellationToken).ConfigureAwait(false);

            string response = await rcon.ExecuteCommandAsync(instance.RconPlayersCommand, cancellationToken).ConfigureAwait(false);
            await rcon.DisconnectAsync().ConfigureAwait(false);

            var currentPlayers = RconPlayerResponseParser.Parse(response, instance.RconPlayersRegex);
            var currentDict = new Dictionary<string, RconPlayerResponseParser.PlayerEntry>(StringComparer.Ordinal);
            foreach (var player in currentPlayers)
                currentDict[player.Key] = player;

            // Detect joins: players in current poll but not in previous poll
            foreach (var player in currentPlayers)
            {
                if (!previousPlayers.ContainsKey(player.Key))
                {
                    string sessionKey = PlayerSessionMap.ComputeSessionKey(null, null, player.Id, player.Name) ?? player.Key;
                    if (sessionStore.Join(name, sessionKey, player.Id, player.Name, null))
                    {
                        EmitJoined(name, player.Id, player.Name, sessionKey);
                    }
                }
            }

            // Detect leaves: players in previous poll but not in current poll
            foreach (var prev in previousPlayers)
            {
                if (!currentDict.ContainsKey(prev.Key))
                {
                    RconPlayerResponseParser.PlayerEntry gone = prev.Value;
                    string sessionKey = PlayerSessionMap.ComputeSessionKey(null, null, gone.Id, gone.Name) ?? prev.Key;
                    var resolved = sessionStore.Leave(name, sessionKey, gone.Id, gone.Name, null);
                    if (resolved is { } r)
                    {
                        EmitLeft(name, r.Id ?? gone.Id, r.Name ?? gone.Name, sessionKey, null);
                    }
                }
            }

            // Update state
            _previousPollState[name] = currentDict;
            ReportPollRecovered(name);
        }
        catch (RconException ex)
        {
            ReportPollFailure(name, ex, "RCON poll failed for {Instance} on port {Port}", name, port);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportPollFailure(name, ex, "Unexpected error during RCON poll for {Instance}", name);
        }
        finally
        {
            // Stamped on every outcome, not just success. Left unstamped, a failing instance never
            // satisfies ShouldPoll's interval and is retried on each 1 Hz tick instead of on its
            // configured cadence — so the one instance least likely to answer is polled the hardest.
            _lastPollTimestamps[name] = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Log a failed poll once per outage rather than once per attempt. A misconfigured or unreachable
    /// RCON does not resolve itself between polls, so repeating the same warning with its stack trace
    /// every interval buries the journal without adding an observation; the recovery is logged too, so
    /// the quiet stretch between them stays bounded by something visible.
    /// </summary>
    private void ReportPollFailure(string instanceName, Exception ex, string message, params object?[] args)
    {
        string signature = $"{ex.GetType().Name}: {ex.Message}";
        bool firstOfThisOutage = !_lastFailureSignature.TryGetValue(instanceName, out string? previous)
            || !string.Equals(previous, signature, StringComparison.Ordinal);
        _lastFailureSignature[instanceName] = signature;

        if (firstOfThisOutage)
            logger.LogWarning(ex, message, args);
        else
            logger.LogDebug(ex, message, args);
    }

    /// <summary>
    /// Log a per-instance configuration complaint the first time it is seen. The condition is a
    /// property of the blueprint, so it holds on every pass until someone edits it — repeating it at
    /// the poll cadence would say nothing new.
    /// </summary>
    private void WarnOnce(string instanceName, string message, params object?[] args)
    {
        if (_warned.TryAdd(instanceName, message))
            logger.LogWarning(message, args);
    }

    private void ReportPollRecovered(string instanceName)
    {
        if (_lastFailureSignature.TryRemove(instanceName, out _))
            logger.LogInformation("RCON poll recovered for {Instance}", instanceName);
    }

    private bool ShouldPoll(string instanceName, int intervalSeconds)
    {
        if (!_lastPollTimestamps.TryGetValue(instanceName, out long lastPoll))
            return true; // never polled → poll now

        long elapsed = Environment.TickCount64 - lastPoll;
        return elapsed >= intervalSeconds * 1000L;
    }

    /// <summary>
    /// The player set this poller would have recorded, reconstructed from the shared session map. Only
    /// sessions carrying an id are usable — this poller diffs on the id RCON reports, and a session
    /// without one could never appear on the other side of that diff.
    /// </summary>
    private Dictionary<string, RconPlayerResponseParser.PlayerEntry> SeedFromSessionMap(string instanceName)
    {
        var seeded = new Dictionary<string, RconPlayerResponseParser.PlayerEntry>(StringComparer.Ordinal);
        foreach (PlayerSessionMap.Session session in sessionStore.GetSessions(instanceName))
        {
            // Keyed the way a parsed entry is keyed, so a session seeded here and the same player
            // read back from RCON land on one entry rather than two.
            string? key = !string.IsNullOrEmpty(session.Id) ? session.Id : session.Name;
            if (string.IsNullOrEmpty(key))
                continue;

            // The name stays whatever the session holds, including nothing. Substituting the key for
            // an absent one presents an id as a display name, and the leave this seeds would announce
            // a player whose name is a number nobody chose.
            seeded[key] = new RconPlayerResponseParser.PlayerEntry(session.Id, session.Name);
        }
        return seeded;
    }

    private void EmitJoined(string instanceName, string? playerId, string? playerName, string sessionKey)
    {
        // RCON reports who is connected, never from where — the address stays null rather than blank.
        journal.Player(EventJoined, instanceName, playerId, playerName, playerAddr: null, sessionKey, reason: null);

        logger.LogInformation(
            "recorded {Event} for {Instance} (session={SessionKey} id={Id} name={Name})",
            EventJoined, instanceName, sessionKey, playerId ?? "<none>", playerName ?? "<none>");
    }

    private void EmitLeft(string instanceName, string? playerId, string? playerName, string sessionKey, string? reason)
    {
        journal.Player(
            EventLeft, instanceName, playerId, playerName,
            playerAddr: null, sessionKey, reason ?? string.Empty);

        logger.LogInformation(
            "recorded {Event} for {Instance} (session={SessionKey} id={Id} name={Name})",
            EventLeft, instanceName, sessionKey, playerId ?? "<none>", playerName ?? "<none>");
    }

    private string ResolveInstancesDir()
    {
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/var/lib", ".local", "share");

        return Path.Combine(dataHome, "kgsm", "instances");
    }

    private static IEnumerable<string> DiscoverInstanceNames(string root)
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
            return Directory.EnumerateDirectories(dir);
        }
        catch (Exception)
        {
            return [];
        }
    }
}
