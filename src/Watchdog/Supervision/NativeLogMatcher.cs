using System.Text.RegularExpressions;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The native counterpart of the container in-image shim's matching step: applies a blueprint's
/// <c>player_joined_regex</c> / <c>player_left_regex</c> (delivered on the <see cref="Instance"/> via the
/// kgsm-lib chokepoint) to one raw game-log line and decides whether to emit a presence event. Pure +
/// side-effect-free (constructed once per instance, then <see cref="Match"/> per line) so every branch
/// is unit-testable without files or sockets, mirroring <see cref="PlayerPresenceParser"/>; it returns
/// the SAME <see cref="PlayerPresenceParser.ParseResult"/> shape and the same dash-form event names, so
/// downstream emit is identical to the container path.
/// <para>
/// <b>Named groups (player-presence contract §4).</b> <c>id</c>/<c>name</c>/<c>addr</c>/<c>key</c>/
/// <c>reason</c> are all optional; a group that didn't participate, or captured only whitespace, is
/// treated as absent (null) — never a fabricated empty value.
/// </para>
/// <para>
/// <b>Honesty rules (mirroring the shim).</b> An empty/unset pattern disables that detection (no event
/// invented). An invalid regex disables that detection and records a warning (the ingester logs it) —
/// never crashes. On a <b>join</b>, a line that matches but captures none of <c>id</c>/<c>name</c>/
/// <c>addr</c> is dropped (the at-least-one-of-id/name/addr rule — a session with no human-meaningful
/// field is not a roster entry; <c>key</c> alone doesn't count, it's an opaque correlation token, not an
/// identity). A <b>leave</b> has no such guard here — leaves are frequently bare tokens (romestead's
/// addr-only, Valheim/Core Keeper's key-only); the per-instance session map (<see cref="PlayerSessionMap"/>)
/// is what resolves a leave's identity from the matching join, or honestly skips it. A line that matches
/// no pattern is silently ignored (the normal case — most log lines aren't presence lines — so it is NOT
/// a loggable drop).
/// </para>
/// <para>
/// <b>AOT + safety.</b> Patterns are blueprint-supplied (not compile-time constants), so a runtime
/// <see cref="Regex"/> is used — deliberately <em>not</em> <see cref="RegexOptions.Compiled"/> (it is
/// silently interpreted under Native AOT, no benefit). A 100 ms match timeout guards against a
/// catastrophic-backtracking (ReDoS) blueprint pattern.
/// </para>
/// </summary>
internal sealed class NativeLogMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Regex? _joined;
    private readonly Regex? _left;

    /// <summary>Invalid-pattern messages for the ingester to log once when it builds this matcher (the
    /// shim's "log + disable" behaviour, surfaced here rather than logged inside this pure type).</summary>
    public IReadOnlyList<string> Warnings { get; }

    public NativeLogMatcher(string joinedPattern, string leftPattern)
    {
        var warnings = new List<string>();
        _joined = Compile(joinedPattern, "player_joined_regex", warnings);
        _left = Compile(leftPattern, "player_left_regex", warnings);
        Warnings = warnings;
    }

    /// <summary>True when at least one pattern compiled — i.e. this instance has live native detection.</summary>
    public bool Enabled => _joined is not null || _left is not null;

    private static Regex? Compile(string pattern, string label, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return null; // unset → that detection disabled (honest unknown, no event)
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            warnings.Add($"{label} is not a valid .NET regex — that detection is disabled: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Match one log line. Tries joined then left. Returns an emit result on a valid capture, a drop with
    /// a reason on a real anomaly (matched-but-both-null, or a match timeout) for the caller to log, or a
    /// SILENT non-emit (<see cref="PlayerPresenceParser.ParseResult.Emit"/> false +
    /// <see cref="PlayerPresenceParser.ParseResult.DropReason"/> null) when nothing matched.
    /// </summary>
    public PlayerPresenceParser.ParseResult Match(string line)
    {
        if (string.IsNullOrEmpty(line))
            return Ignore;

        return TryMatch(_joined, PlayerPresenceParser.EventPlayerJoined, line, isJoin: true)
            ?? TryMatch(_left, PlayerPresenceParser.EventPlayerLeft, line, isJoin: false)
            ?? Ignore;
    }

    // A non-emit the ingester does NOT log: the overwhelmingly common "this line is not a presence line".
    private static PlayerPresenceParser.ParseResult Ignore => new(false, null, null, null, null, null, null, null);

    private static PlayerPresenceParser.ParseResult? TryMatch(Regex? rx, string eventName, string line, bool isJoin)
    {
        if (rx is null)
            return null;

        Match m;
        try
        {
            m = rx.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return PlayerPresenceParser.ParseResult.Drop("regex match timed out (possible ReDoS pattern)");
        }

        if (!m.Success)
            return null;

        string? id = Group(m, "id");
        string? name = Group(m, "name");
        string? addr = Group(m, "addr");
        string? key = Group(m, "key");
        string? reason = Group(m, "reason");

        // The identity guard is join-only: a leave resolves its identity from the session map (or
        // honestly skips), so a bare-token leave (addr-only / key-only) is expected, not an anomaly.
        if (isJoin && id is null && name is null && addr is null)
            return PlayerPresenceParser.ParseResult.Drop(
                "matched but captured no identity (id/name/addr all absent — at-least-one-non-null violated)");

        return PlayerPresenceParser.ParseResult.Event(eventName, id, name, addr, key, reason);
    }

    // A named group that didn't participate, or captured only whitespace, is treated as absent (null) —
    // so a pattern with an optional (?<id>) that didn't fire never surfaces a fabricated empty id.
    private static string? Group(Match m, string name)
    {
        Group g = m.Groups[name];
        return g.Success && !string.IsNullOrWhiteSpace(g.Value) ? g.Value : null;
    }
}
