using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Watchdog.Cgroup;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The watchdog's <b>native role</b> for player presence AND boot-readiness: an always-on host-side
/// ingester that tails each NATIVE instance's own game log and emits (a) player join/left wire events —
/// the same events the container path produces, so the contract (and every downstream consumer) is
/// identical; only the mechanism differs (containers self-report NDJSON via an in-image shim, native
/// instances are matched here) — and (b) <c>instance-ready</c>, the "finished booting / joinable" signal
/// the Control Panel uses to flip a server from "Starting" to "Running".
/// <para>
/// It is purely a file reader + event forwarder, and needs <b>no spawn-path change</b>: the watchdog's
/// <see cref="SpawnEngine"/> already execs the native game with its stdout+stderr appended to
/// <c>instance.LogFile</c> (a stable path), so detection is a tail of that file. The blueprint's
/// <c>player_joined_regex</c>/<c>player_left_regex</c>/<c>startup_success_regex</c> arrive on the
/// <see cref="Instance"/> via the kgsm-lib chokepoint (<see cref="IInstanceService.GetInstanceInfo"/>) —
/// KGSM materialises them into the instance config, which <c>instances info --json</c> emits. The player
/// match is the pure <see cref="NativeLogMatcher"/> (the .NET analog of the in-image Perl shim); the
/// readiness match is the pure <see cref="NativeReadinessMatcher"/> (the .NET analog of the bash
/// reference's <c>watchers.logs.sh</c> whole-file grep).
/// </para>
/// <para>
/// Poll-driven (mirroring <see cref="PlayerPresenceIngester"/> and <see cref="CrashWatcher"/>). Each
/// instance's <see cref="EventChannelTail"/> uses <c>primeAtEnd</c>: the FIRST attach seeks to the log's
/// current end, so a long-running append-only native log is never replayed from the start (which would
/// flood stale joins) — only lines written after we attach are matched. Metadata (runtime, log path,
/// patterns) is static config, so it is fetched once per instance and cached; a non-native instance, or
/// one with genuinely nothing to detect (no player patterns, no readiness pattern, and no readiness
/// fallback — see <see cref="BuildWatch"/>), is cached as "skip" and never re-fetched. Tailing a
/// <em>stopped</em> instance is harmless (no appends ⇒ no events; the parked tail catches the whole next
/// session when it appends), so this is decoupled from supervision/run-state, exactly like the container
/// ingester is decoupled from kgsm-lib enumeration.
/// </para>
/// <para>
/// <b>Correlation (player-presence contract §4).</b> Each watch owns a <see cref="PlayerSessionMap"/>:
/// join inserts-if-absent (dedups a doubled join line), leave resolves-and-evicts (dedups a repeated
/// leave burst and recovers the identity a bare-token leave line doesn't itself carry). The map is
/// cleared whenever <see cref="EventChannelTail.LastReadResetSession"/> reports the log rolled to a
/// fresh session — every prior session is gone with it.
/// </para>
/// <para>
/// <b>Readiness (per instance start/session).</b> See <see cref="ProcessReadiness"/>: the authoritative
/// "a fresh run started" edge is the instance's cgroup transitioning not-populated → populated (read via
/// <see cref="CgroupManager.IsPopulated"/>, the SAME child-inclusive liveness signal <c>CrashWatcher</c>
/// polls) — this is universal across every spawn path (manual start, boot autostart, crash-restart,
/// daemon-restart re-adopt) without depending on log-rotation semantics. It also re-arms on
/// <see cref="EventChannelTail.LastReadResetSession"/> as a defensive second trigger (the bash reference
/// truncates <c>instance_log_file</c> to a fresh inode on every start — <c>SpawnEngine</c> currently
/// appends instead, a known, separately-tracked divergence — so this covers either behavior). On a fresh
/// edge: an EMPTY <c>startup_success_regex</c> emits <c>instance-ready</c> immediately (honest fallback —
/// no distinct readiness signal, so "ready" == "observed started", never a fabricated delay); a non-empty
/// valid pattern first does a one-shot whole-file scan (<see cref="NativeReadinessMatcher.MatchesExistingContent"/>)
/// to catch a late attach where the ready line already went by (daemon hot-swap mid-boot, or attaching to
/// an already-running instance), then the normal tail matches every new line going forward. Exactly one
/// <c>instance-ready</c> fires per run (<c>ReadyFired</c> latch), re-armed only by the next start edge.
/// </para>
/// </summary>
internal sealed class NativePlayerPresenceIngester(
    WatchdogOptions options,
    IInstanceService instances,
    IEventManagementService events,
    PlayerSessionStore sessionStore,
    CgroupManager cgroups,
    ILogger<NativePlayerPresenceIngester> logger) : BackgroundService
{
    /// <summary>
    /// A live watch for one native instance: the shared tail (null when nothing needs to read the log —
    /// a pure immediate-readiness instance with no player patterns), the player matcher, the readiness
    /// matcher, and the mutable per-run readiness state. Mutable (not a positional record) because
    /// <see cref="LastPopulated"/>/<see cref="ReadyFired"/> change every tick.
    /// </summary>
    private sealed class NativeWatch(EventChannelTail? tail, NativeLogMatcher matcher, NativeReadinessMatcher readiness, string logFile)
    {
        public EventChannelTail? Tail { get; } = tail;
        public NativeLogMatcher Matcher { get; } = matcher;
        public NativeReadinessMatcher Readiness { get; } = readiness;
        public string LogFile { get; } = logFile;

        /// <summary>Cgroup-populated state observed on the PREVIOUS tick; null before the first observation
        /// (so a late attach to an already-running instance is still treated as a start edge).</summary>
        public bool? LastPopulated { get; set; }

        /// <summary>Latched true once <c>instance-ready</c> has fired for the CURRENT run; cleared only on
        /// the next start edge (see <see cref="NativePlayerPresenceIngester.ProcessReadiness"/>).</summary>
        public bool ReadyFired { get; set; }
    }

    // name -> live watch (native + something to detect: player patterns, readiness pattern, or the
    // immediate-readiness fallback). Survives across ticks so the tail cursor + readiness latch resume.
    private readonly Dictionary<string, NativeWatch> _watches = new(StringComparer.Ordinal);

    // The "finished booting / joinable" signal the Control Panel uses to flip a server from "Starting"
    // to "Running". Dash form to match this repo's existing event constants (mirrors
    // InstanceSupervisor.EventStarted etc.); kgsm's `events emit` normalises dashes→underscores.
    private const string EventReady = "instance-ready";

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

            ProcessReadiness(name, watch);

            if (watch.Tail is null)
                continue; // pure immediate-readiness, cgroup-poll-driven only — nothing to tail

            IReadOnlyList<string> lines = watch.Tail.ReadNewLines();
            if (watch.Tail.LastReadResetSession)
            {
                // The log rolled to a fresh session (inode change past the first attach, or a same-inode
                // reuse) — every session this instance's map was tracking is gone with the old file.
                sessionStore.Reset(name);
                // Defensive second re-arm trigger (see class remarks): the cgroup populated-edge is the
                // authoritative one, but a genuine log rotation/truncation is also unambiguously a fresh
                // run — re-arming here costs nothing when the cgroup edge already did it.
                watch.ReadyFired = false;
                logger.LogDebug("native player-presence session map reset for {Instance} (new log session)", name);
            }

            foreach (string line in lines)
            {
                HandleLine(name, line, watch.Matcher);

                if (watch.Readiness.Enabled && !watch.ReadyFired && watch.Readiness.IsMatch(line))
                {
                    watch.ReadyFired = true;
                    EmitReady(name);
                }
            }
        }
    }

    /// <summary>
    /// Detect a fresh run of <paramref name="name"/> (cgroup not-populated → populated, INCLUDING the
    /// very first observation of an already-running instance — a late attach) and, on that edge, fire
    /// <c>instance-ready</c> either immediately (empty <c>startup_success_regex</c>) or via a one-shot
    /// whole-file scan that catches a ready line already logged before this edge was observed. Never
    /// re-fires within the same run — <see cref="NativeWatch.ReadyFired"/> is the latch, re-armed only
    /// here (or by the tail's own session-reset, see <see cref="IngestOnce"/>).
    /// </summary>
    private void ProcessReadiness(string name, NativeWatch watch)
    {
        bool populated = cgroups.IsPopulated(name);
        bool startEdge = watch.LastPopulated != true && populated;
        watch.LastPopulated = populated;

        if (!startEdge)
            return;

        watch.ReadyFired = false; // a fresh run (or the first-ever observation) — re-arm

        if (watch.Readiness.IsImmediate)
        {
            // Honest fallback (req. 3): no distinct readiness signal configured for this blueprint, so
            // "ready" == "observed started" — never a fabricated delay.
            watch.ReadyFired = true;
            EmitReady(name);
            return;
        }

        if (watch.Readiness.Enabled && watch.Tail is not null &&
            watch.Readiness.MatchesExistingContent(watch.LogFile))
        {
            // Late attach: the ready line was already logged before we noticed the start edge (a daemon
            // hot-swap/restart mid-boot, or attaching to an already-running instance) — a one-shot scan
            // independent of the tail's own offset, so it never disturbs its bookkeeping.
            watch.ReadyFired = true;
            EmitReady(name);
        }
        // else: pattern-based and not yet matched — the normal per-line check in IngestOnce catches it
        // the moment the game appends its ready line.
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

        var readiness = new NativeReadinessMatcher(instance.StartupSuccessRegex);
        if (readiness.Warning is not null)
            logger.LogWarning("native readiness for {Instance}: {Warning}", name, readiness.Warning);

        // Widened enable gate (was: player patterns alone): a watch is worth building when there is
        // player detection, OR a valid readiness pattern, OR the honest immediate-readiness fallback
        // applies (empty pattern). Many games (factorio, minecraft, terraria) have a
        // startup_success_regex but NO player_*_regex — they must NOT be skipped just because presence
        // detection has nothing to do. Only a genuinely broken/absent config (no player patterns AND an
        // INVALID, non-empty readiness pattern) has nothing left to detect.
        if (!matcher.Enabled && !readiness.Enabled && !readiness.IsImmediate)
        {
            _skip.Add(name); // no (valid) patterns of any kind → honest unknown, no event ever fabricated
            logger.LogDebug("native player-presence+readiness disabled for {Instance} (no valid patterns)", name);
            return null;
        }

        // A log file is needed for anything that reads lines (player patterns, or a pattern-based
        // readiness regex) — but NOT for the pure immediate-readiness fallback, which only needs the
        // cgroup populated-edge (see ProcessReadiness). So a missing log path disables pattern-based
        // reading without necessarily skipping the instance outright.
        bool needsLog = matcher.Enabled || readiness.Enabled;
        EventChannelTail? tail = null;
        if (needsLog)
        {
            if (string.IsNullOrEmpty(instance.LogFile))
            {
                logger.LogWarning(
                    "native player-presence/readiness for {Instance}: patterns set but log_file is empty — pattern-based detection skipped",
                    name);
                if (!readiness.IsImmediate)
                {
                    _skip.Add(name); // nothing left to detect without a log file
                    return null;
                }
            }
            else
            {
                tail = new EventChannelTail(instance.LogFile, primeAtEnd: true);
            }
        }

        logger.LogInformation(
            "native player-presence/readiness watching {Instance} (log {Log})", name, instance.LogFile);
        return new NativeWatch(tail, matcher, readiness, instance.LogFile);
    }

    private void HandleLine(string instanceName, string line, NativeLogMatcher matcher)
    {
        PlayerPresenceParser.ParseResult result = matcher.Match(line);
        if (!result.Emit)
        {
            // DropReason null = a normal non-matching line (the common case) — never logged. A non-null
            // reason is a real anomaly (matched-but-no-identity, or a regex timeout) worth a warning.
            if (result.DropReason is not null)
                logger.LogWarning(
                    "dropping native presence line for {Instance} ({Reason}): {Line}",
                    instanceName, result.DropReason, Truncate(line));
            return;
        }

        // Contract §4: sessionKey = first-non-blank(key, addr, id, name). A join that passed the
        // matcher's identity guard always yields one; a bare leave line with none of the four captured
        // (should not happen for the four validated games, but never assumed) has nothing to key the map
        // on and is skipped rather than risk keying on an empty string.
        string? sessionKey = PlayerSessionMap.ComputeSessionKey(result.Key, result.PlayerAddr, result.PlayerId, result.PlayerName);
        if (sessionKey is null)
            return;

        if (result.EventName == PlayerPresenceParser.EventPlayerJoined)
        {
            if (!sessionStore.Join(instanceName, sessionKey, result.PlayerId, result.PlayerName, result.PlayerAddr))
                return; // already tracked — a doubled join line (Valheim logs every line twice)

            EmitJoined(instanceName, result.PlayerId, result.PlayerName, result.PlayerAddr, sessionKey);
        }
        else
        {
            PlayerSessionMap.Session? resolved = sessionStore.Leave(instanceName, sessionKey, result.PlayerId, result.PlayerName, result.PlayerAddr);
            if (resolved is not { } r)
                return; // nothing to attribute (map miss + no id/name on the line) — honest skip, never fabricate

            EmitLeft(instanceName, r.Id, r.Name, r.Addr, sessionKey, result.Reason);
        }
    }

    /// <summary>
    /// Emit <c>instance-player-joined</c> per contract §1: <c>instanceName, playerId, playerName,
    /// playerAddr, sessionKey</c> — five positional params, no trailing reason slot (joins don't have
    /// one).
    /// </summary>
    private void EmitJoined(string instanceName, string? playerId, string? playerName, string? playerAddr, string sessionKey)
        => Emit(
            PlayerPresenceParser.EventPlayerJoined, instanceName, sessionKey,
            $"id={Display(playerId)} name={Display(playerName)} addr={Display(playerAddr)}",
            [instanceName, playerId ?? string.Empty, playerName ?? string.Empty, playerAddr ?? string.Empty, sessionKey]);

    /// <summary>
    /// Emit <c>instance-player-left</c> per contract §1: <c>instanceName, playerId, playerName,
    /// playerAddr, sessionKey, reason</c> — six positional params, the trailing slot the join event
    /// doesn't have.
    /// </summary>
    private void EmitLeft(string instanceName, string? playerId, string? playerName, string? playerAddr, string sessionKey, string? reason)
        => Emit(
            PlayerPresenceParser.EventPlayerLeft, instanceName, sessionKey,
            $"id={Display(playerId)} name={Display(playerName)} addr={Display(playerAddr)} reason={Display(reason)}",
            [instanceName, playerId ?? string.Empty, playerName ?? string.Empty, playerAddr ?? string.Empty, sessionKey, reason ?? string.Empty]);

    private static string Display(string? value) => string.IsNullOrEmpty(value) ? "<none>" : value;

    /// <summary>
    /// Emit <c>instance-ready</c>: a single positional arg (the instance name) per the wire form kgsm-lib
    /// / kgsm-api already register (<see cref="InstanceReadyData"/> upstream) — normalises dash→underscore
    /// like every other event here. Same provenance + best-effort semantics as <see cref="Emit"/> (a
    /// failed emit is logged and swallowed, never retried — the latch stays set so it is not
    /// double-attempted on the next tick; a genuinely missed emit is exactly as recoverable as a missed
    /// player-presence emit today).
    /// </summary>
    private void EmitReady(string instanceName)
    {
        try
        {
            events.EmitWithProvenance(EventReady, actor: "system", origin: "system", [instanceName]);
            logger.LogInformation("emitted {Event} for {Instance}", EventReady, instanceName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to emit {Event} for {Instance} (event dropped)", EventReady, instanceName);
        }
    }

    /// <summary>
    /// Emit one presence event through kgsm-lib, stamped <c>actor="system" / origin="system"</c> — an
    /// autonomous observation no human drove, identical to the container ingester and the supervisor's
    /// system/system emits. <b>Why not <c>actor:null</c>:</b> a null actor makes kgsm-lib omit
    /// <c>KGSM_EVENT_ACTOR</c> and kgsm's payload builder then falls back to the daemon's OS user — a
    /// fabricated human identity on an autonomous event. Null fields pass as empty string (a string-based
    /// emit can't carry a literal JSON null mid-args; kgsm maps empty→null); <paramref name="parameters"/>
    /// is pre-built by <see cref="EmitJoined"/>/<see cref="EmitLeft"/> so each event carries exactly its
    /// contract arity. Best-effort: a failed emit is logged and swallowed, never crashes the ingester.
    /// </summary>
    private void Emit(string eventName, string instanceName, string sessionKey, string detail, string[] parameters)
    {
        try
        {
            events.EmitWithProvenance(eventName, actor: "system", origin: "system", parameters);

            logger.LogInformation(
                "emitted {Event} for {Instance} (session={SessionKey} {Detail})",
                eventName, instanceName, sessionKey, detail);
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
