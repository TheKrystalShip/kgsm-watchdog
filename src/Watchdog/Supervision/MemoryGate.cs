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
/// The two knobs are read from <b>kgsm's</b> config, not from the daemon's own settings, so the host
/// has one answer rather than two that can disagree. They arrive through
/// <see cref="IConfigService"/> — the C#-to-engine chokepoint — never by parsing the ini here.
/// </para>
/// <para>
/// <b>There is no override.</b> The CLI has <c>--force</c> because a person at a terminal can judge
/// that a blueprint's declared figure overstates what a game really uses. The daemon has nobody to
/// make that judgement, so it never overrules itself.
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

    private readonly Lock _gate = new();
    private (bool Enabled, int HeadroomMb, DateTime ReadAt)? _settings;
    private readonly Dictionary<string, (int? MinRamMb, DateTime ReadAt)> _blueprintRam = new(StringComparer.Ordinal);

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
    }

    /// <summary>The verdict and, when refused, the sentence explaining it in the same shape kgsm's does.</summary>
    public readonly record struct Decision(Verdict Verdict, string? Reason)
    {
        public bool IsRefused => Verdict == Verdict.Refused;
    }

    private static readonly Decision AllowedDecision = new(Verdict.Allowed, null);

    /// <summary>
    /// Would starting <paramref name="spec"/> leave the node with less than the configured floor?
    /// </summary>
    /// <remarks>
    /// Allows in every case where it cannot answer — gate off, nothing declared, meminfo unreadable.
    /// A supervisor that refused to start game servers because it could not read its own config would
    /// be a worse outage than the one the gate exists to prevent.
    /// </remarks>
    public Decision Evaluate(Instance spec)
    {
        if (spec is null) return AllowedDecision;

        (bool enabled, int headroomMb) = Settings();
        if (!enabled) return AllowedDecision;

        int? required = RequirementMb(spec);
        if (required is null)
            return new Decision(Verdict.NotChecked,
                "no memory requirement declared (no memory_cap_mb, no blueprint min_ram_mb)");

        int? available = MemoryReader.AvailableMb();
        if (available is null)
            return new Decision(Verdict.NotChecked, "could not read MemAvailable from /proc/meminfo");

        int remaining = available.Value - required.Value;
        if (remaining >= headroomMb) return AllowedDecision;

        return new Decision(Verdict.Refused,
            $"needs {required.Value}MB, the node has {available.Value}MB available, and starting it "
            + $"would leave {remaining}MB against a required floor of {headroomMb}MB");
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
                && DateTime.UtcNow - cached.ReadAt < CacheTtl)
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
            _blueprintRam[blueprint] = (minRam, DateTime.UtcNow);
        }
        return minRam;
    }

    // The two kgsm keys, cached. A malformed value falls back to the default rather than disabling the
    // reserve silently — a typo in config.ini must not quietly remove the protection.
    private (bool Enabled, int HeadroomMb) Settings()
    {
        lock (_gate)
        {
            if (_settings is { } s && DateTime.UtcNow - s.ReadAt < CacheTtl)
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
            _settings = (enabled, headroom, DateTime.UtcNow);
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
