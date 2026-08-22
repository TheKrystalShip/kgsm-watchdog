using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Whether the node has room to start an instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>The daemon needs its own copy of this check because it is the only thing that runs some of these
/// starts.</b> The kgsm CLI gates the starts a person types; the boot autostart and the crash-restart
/// never pass through it. A gate that lived only in the CLI would be absent from exactly the case that
/// motivates it — an instance restarting into a node that has filled up since it last ran.
/// </para>
/// <para>
/// <b>The gate answers over a SET of starts, not one at a time.</b> <c>MemAvailable</c> lags a server
/// that has just spawned — a process two seconds old has allocated almost nothing, and a JVM grows into
/// its heap over minutes — so a batch of starts judged on the raw reading alone each pass honestly and
/// collectively commit far past the floor. Every allowed spawn therefore takes a <b>reservation</b> for
/// the figure it was judged on, and <see cref="Evaluate"/> subtracts what is outstanding: the next
/// instance is judged against what the node will have once the ones already starting have taken what
/// they asked for. <see cref="Release"/> drops a reservation the moment the instance reports ready, and
/// on every path where it will never report ready.
/// </para>
/// <para>
/// The two knobs are read from <b>kgsm's</b> config, not from the daemon's own settings, so the host
/// has one answer rather than two that can disagree. They arrive through
/// <see cref="IConfigService"/> — the C#-to-engine chokepoint — never by parsing the ini here.
/// </para>
/// <para>
/// <b>The daemon never overrules itself.</b> It has nobody to ask, so the autostart and the
/// crash-restart take the verdict as final. An <em>explicit</em> start can carry a person's
/// <c>--force</c>, which is a different thing: the judgement that a blueprint's declared figure
/// overstates what a game really uses is one a human at a terminal makes, and forcing only ever
/// arrives from there. A forced start still takes its reservation — see <see cref="TryReserve"/>.
/// </para>
/// </remarks>
public sealed class MemoryGate(
    IConfigService config,
    IBlueprintService blueprints,
    ILogger<MemoryGate> logger)
{
    /// <summary>kgsm's key for the master switch. Shared with the CLI; see kgsm's <c>[resources]</c>.</summary>
    private const string EnabledKey = "enable_memory_gate";

    /// <summary>kgsm's key for the reserve that must survive a start.</summary>
    private const string HeadroomKey = "memory_gate_headroom_mb";

    /// <summary>What both sides fall back to when the key is absent — a host whose config predates the
    /// gate is protected, not unprotected. Must match kgsm's coded defaults.</summary>
    private const bool DefaultEnabled = true;
    private const int DefaultHeadroomMb = 1024;

    /// <summary>
    /// How long a config or blueprint reading is reused. Every read costs a kgsm invocation, and the
    /// crash-restart path can ask repeatedly while an instance is looping; a minute is short enough
    /// that an operator's config edit takes effect while they are still watching.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest a reservation is held without the instance reporting ready.
    /// </summary>
    /// <remarks>
    /// Readiness is what releases a reservation; this is only the leak guard behind it, for the starts
    /// that never report one — a blueprint whose <c>startup_success_regex</c> does not compile, a boot
    /// that hangs before the game ever prints its ready line. It only has to be generous: holding a
    /// reservation too long delays another start, while dropping it too early re-opens the over-commit
    /// window it exists to close. Ten minutes sits well past the slow end of a game-server boot (a large
    /// modpack or a big world load runs into minutes) without leaving a leaked entry standing for a
    /// meaningful part of a day.
    /// </remarks>
    private static readonly TimeSpan ReservationMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Guards the two caches and the reservation ledger. Not the supervisor's own spawn gate — that one
    /// serialises whole start/stop verbs; this one is held only for the few statements that touch these
    /// dictionaries, never across an engine read.
    /// </summary>
    private readonly Lock _gate = new();
    private (bool Enabled, int HeadroomMb, DateTime ReadAt)? _settings;
    private readonly Dictionary<string, (int? MinRamMb, DateTime ReadAt)> _blueprintRam = new(StringComparer.Ordinal);

    /// <summary>
    /// Memory committed to instances that have been allowed to spawn but have not yet reported ready,
    /// keyed by instance name. One entry per instance: a re-spawn replaces the previous figure rather
    /// than stacking a second one.
    /// </summary>
    private readonly Dictionary<string, (int Mb, DateTime TakenAt)> _reservations = new(StringComparer.Ordinal);

    /// <summary>The node's own <c>MemAvailable</c> reading, seamed so a test can pose a node of a known size.</summary>
    private readonly Func<int?> _available = MemoryReader.AvailableMb;

    /// <summary>Clock behind the two cache TTLs and the reservation backstop.</summary>
    private readonly TimeProvider _clock = TimeProvider.System;

    /// <summary>Test seam: a posed node reading and a controllable clock. Production always reads
    /// <c>/proc/meminfo</c> and the system clock.</summary>
    internal MemoryGate(
        IConfigService config,
        IBlueprintService blueprints,
        ILogger<MemoryGate> logger,
        Func<int?> available,
        TimeProvider clock) : this(config, blueprints, logger)
    {
        _available = available;
        _clock = clock;
    }

    /// <summary>The gate's answer. <see cref="Refused"/> is the only one that stops a spawn.</summary>
    public enum Verdict
    {
        /// <summary>The node has room, or the gate is off.</summary>
        Allowed,

        /// <summary>Nothing was declared to compare against, so the check could not run. Allowed, and
        /// deliberately distinct from <see cref="Allowed"/> so the log can say which happened.</summary>
        NotChecked,

        /// <summary>Starting this would leave the node below the headroom floor.</summary>
        Refused,

        /// <summary>Starting this would leave the node below the floor and it is being started anyway,
        /// on a person's explicit instruction. Not a refusal — the spawn proceeds — and deliberately
        /// distinct from <see cref="Allowed"/> so the log can say which happened.</summary>
        Forced,
    }

    /// <summary>The verdict and, when refused, the sentence explaining it in the same shape kgsm's does.</summary>
    public readonly record struct Decision(Verdict Verdict, string? Reason)
    {
        public bool IsRefused => Verdict == Verdict.Refused;
    }

    private static readonly Decision AllowedDecision = new(Verdict.Allowed, null);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Would starting <paramref name="spec"/> leave the node with less than the configured floor, given
    /// what is already reserved for the instances currently starting? Takes no reservation itself.
    /// </summary>
    /// <remarks>
    /// Allows in every case where it cannot answer — gate off, nothing declared, meminfo unreadable.
    /// A supervisor that refused to start game servers because it could not read its own config would
    /// be a worse outage than the one the gate exists to prevent. An unanswerable check is unanswerable
    /// whatever the ledger holds: those cases return before any reservation is subtracted.
    /// </remarks>
    public Decision Evaluate(Instance spec) => Judge(spec, reserveFor: null);

    /// <summary>
    /// Judge <paramref name="spec"/> exactly as <see cref="Evaluate"/> does and, when the answer is not
    /// a refusal, reserve the figure it was judged on against <paramref name="instanceName"/>.
    /// </summary>
    /// <remarks>
    /// One method rather than an evaluate-then-reserve pair, so the reserved figure and the judged
    /// figure cannot differ and no other spawn can be judged between the two. A verdict the gate could
    /// not answer (nothing declared, meminfo unreadable, gate off) reserves nothing: there is no
    /// measured figure to reserve, and inventing one would refuse real starts on a number nobody
    /// declared.
    /// <para>
    /// <paramref name="force"/> carries a person's explicit instruction to proceed regardless. It
    /// changes the verdict from <see cref="Verdict.Refused"/> to <see cref="Verdict.Forced"/> and
    /// <b>still takes the reservation</b>: forcing means going ahead despite the answer, not leaving
    /// the ledger out of it. A forced start that reserved nothing would leave the next instance judged
    /// against memory this one is about to take — the staleness the ledger exists to remove, back at
    /// the moment the node is under the most pressure.
    /// </para>
    /// </remarks>
    public Decision TryReserve(string instanceName, Instance spec, bool force = false)
        => Judge(spec, reserveFor: instanceName, force: force);

    /// <summary>
    /// Drop <paramref name="instanceName"/>'s reservation: the instance reported ready (its memory is
    /// now in the node's own reading, so continuing to subtract would double-count), or it will never
    /// report ready (the spawn failed, it stopped, it crashed, it was deregistered).
    /// </summary>
    /// <remarks>Idempotent. An unknown or already-released name is a no-op, never an error.</remarks>
    public void Release(string instanceName)
    {
        if (string.IsNullOrEmpty(instanceName))
            return;

        (int Mb, DateTime TakenAt) held;
        bool wasHeld;
        lock (_gate)
        {
            wasHeld = _reservations.Remove(instanceName, out held);
        }

        if (wasHeld)
            logger.LogDebug("released {Instance}'s {Mb}MB memory reservation", instanceName, held.Mb);
    }

    /// <summary>Total megabytes currently reserved, excluding anything past the backstop. Read-only.</summary>
    internal int OutstandingReservedMb()
    {
        DateTime now = Now;
        lock (_gate)
        {
            int total = 0;
            foreach (var entry in _reservations.Values)
                if (now - entry.TakenAt < ReservationMaxAge)
                    total += entry.Mb;
            return total;
        }
    }

    private Decision Judge(Instance spec, string? reserveFor, bool force = false)
    {
        if (spec is null) return AllowedDecision;

        (bool enabled, int headroomMb) = Settings();
        if (!enabled) return AllowedDecision;

        int? required = RequirementMb(spec);
        if (required is null)
            return new Decision(Verdict.NotChecked,
                "no memory requirement declared (no memory_cap_mb, no blueprint min_ram_mb)");

        int? available = _available();
        if (available is null)
            return new Decision(Verdict.NotChecked, "could not read MemAvailable from /proc/meminfo");

        DateTime now = Now;
        List<(string Name, int Mb)>? expired = null;
        int reserved;
        int starting;
        int remaining;
        bool fits;

        lock (_gate)
        {
            reserved = 0;
            starting = 0;
            foreach (var entry in _reservations)
            {
                if (now - entry.Value.TakenAt >= ReservationMaxAge)
                {
                    (expired ??= []).Add((entry.Key, entry.Value.Mb));
                    continue;
                }
                reserved += entry.Value.Mb;
                starting++;
            }

            if (expired is not null)
                foreach ((string name, _) in expired)
                    _reservations.Remove(name);

            remaining = available.Value - reserved - required.Value;
            fits = remaining >= headroomMb;

            // A forced start reserves on exactly the same rule as one that fits: what it is about to
            // take is what the next instance must be judged against, whichever way the verdict went.
            if ((fits || force) && reserveFor is { Length: > 0 })
                _reservations[reserveFor] = (required.Value, now);
        }

        // Outside the lock: a start that never reported ready is worth an operator seeing.
        if (expired is not null)
            foreach ((string name, int mb) in expired)
                logger.LogWarning(
                    "{Instance}'s {Mb}MB memory reservation is released after {Minutes} minutes without a ready "
                    + "signal — it was started but never reported ready",
                    name, mb, (int)ReservationMaxAge.TotalMinutes);

        if (fits) return AllowedDecision;

        string committed = starting == 0
            ? ","
            : $" with {reserved}MB committed to {starting} instance(s) still starting,";
        string why =
            $"needs {required.Value}MB, the node has {available.Value}MB available{committed} and starting it "
            + $"would leave {remaining}MB against a required floor of {headroomMb}MB";

        // The sentence is the same either way — what a forced start is going past is worth saying in
        // the same words the refusal would have used.
        return new Decision(force ? Verdict.Forced : Verdict.Refused, why);
    }

    /// <summary>
    /// What this instance is expected to need, or null when nothing is declared.
    /// </summary>
    /// <remarks>
    /// The instance's own cap first: it is the ceiling written to <c>memory.max</c> before the game is
    /// born, so the instance cannot exceed it and it bounds exactly what the node stands to lose. The
    /// blueprint's advisory figure second, because it is vendor-declared and uncurated for many games —
    /// an over-stated one there would refuse a start that would have worked.
    /// <para>
    /// Null means null. No default is substituted: a fabricated requirement would refuse real starts on
    /// a number nobody measured.
    /// </para>
    /// </remarks>
    private int? RequirementMb(Instance spec)
    {
        // 0 is kgsm's spelling of "uncapped" for this key, not a request for no memory.
        if (spec.MemoryCapMb is { } cap and > 0) return cap;

        string blueprint = spec.Blueprint;
        if (string.IsNullOrWhiteSpace(blueprint)) return null;

        lock (_gate)
        {
            if (_blueprintRam.TryGetValue(blueprint, out var cached)
                && Now - cached.ReadAt < CacheTtl)
                return cached.MinRamMb;
        }

        int? minRam = null;
        try
        {
            Blueprint? bp = blueprints.GetInfo(blueprint);
            minRam = bp?.Metadata?.MinRamMb is { } mb and > 0 ? mb : null;
        }
        catch (Exception ex)
        {
            // An unreadable blueprint is an unanswerable check, not a refusal.
            logger.LogDebug(ex, "could not read blueprint {Blueprint} for its memory requirement", blueprint);
        }

        lock (_gate)
        {
            _blueprintRam[blueprint] = (minRam, Now);
        }
        return minRam;
    }

    // The two kgsm keys, cached. A malformed value falls back to the default rather than disabling the
    // reserve silently — a typo in config.ini must not quietly remove the protection.
    private (bool Enabled, int HeadroomMb) Settings()
    {
        lock (_gate)
        {
            if (_settings is { } s && Now - s.ReadAt < CacheTtl)
                return (s.Enabled, s.HeadroomMb);
        }

        bool enabled = DefaultEnabled;
        int headroom = DefaultHeadroomMb;
        try
        {
            string? rawEnabled = config.Get(EnabledKey);
            if (!string.IsNullOrWhiteSpace(rawEnabled))
                enabled = !string.Equals(rawEnabled.Trim(), "false", StringComparison.OrdinalIgnoreCase);

            string? rawHeadroom = config.Get(HeadroomKey);
            if (!string.IsNullOrWhiteSpace(rawHeadroom)
                && int.TryParse(rawHeadroom.Trim(), out int parsed) && parsed >= 0)
                headroom = parsed;
        }
        catch (Exception ex)
        {
            // Falling back to the coded defaults keeps the protection on. Reading the config is how the
            // gate is TUNED; it is not what decides that the gate exists.
            logger.LogDebug(ex, "could not read the memory gate config from kgsm; using defaults");
        }

        lock (_gate)
        {
            _settings = (enabled, headroom, Now);
        }
        return (enabled, headroom);
    }
}

/// <summary>The node's own memory reading.</summary>
internal static class MemoryReader
{
    /// <summary>
    /// <c>MemAvailable</c> in megabytes, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// MemAvailable, deliberately, NOT MemFree. MemFree counts only pages nobody is using at all; it
    /// excludes page cache the kernel hands back the moment something asks for it. On a host that has
    /// been up a while MemFree is a small number beside a large cache, and gating on it would refuse
    /// starts with many gigabytes genuinely available. MemAvailable is the kernel's own estimate of
    /// what a new allocation can have without swapping, which is the question being asked.
    /// <para>
    /// No reading is not a reading of zero — an unreadable /proc/meminfo returns null, and the caller
    /// treats that as a check it could not run.
    /// </para>
    /// </remarks>
    public static int? AvailableMb()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;

                ReadOnlySpan<char> rest = line.AsSpan("MemAvailable:".Length).Trim();
                int space = rest.IndexOf(' ');
                if (space > 0) rest = rest[..space];
                return long.TryParse(rest, out long kb) ? (int)(kb / 1024) : null;
            }
        }
        catch
        {
            // Unreadable for any reason is the same answer: this check cannot run.
        }
        return null;
    }
}
