using System.Text;
using System.Text.RegularExpressions;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Pure readiness detector for one instance's <c>startup_success_regex</c> (kgsm-lib
/// <c>Instance.StartupSuccessRegex</c>) — the .NET analog of the bash reference's
/// <c>watchers.logs.sh</c> <c>__logic_test_log_pattern</c> (whole-file <c>grep -q</c>) and
/// <c>__logic_determine_strategy</c> (empty pattern ⇒ no distinct readiness signal). Mirrors
/// <see cref="NativeLogMatcher"/>'s compile/timeout/honesty rules — 100 ms ReDoS-guard timeout,
/// <see cref="RegexOptions.CultureInvariant"/>, never throws on a bad pattern — but detects a single
/// "has this game finished booting" line rather than player join/left.
/// <para>
/// <b>Honesty rules.</b> An EMPTY pattern is <em>not</em> a config error — it means the blueprint
/// carries no distinct readiness signal, so <see cref="IsImmediate"/> is true and the caller
/// (<see cref="NativePlayerPresenceIngester"/>) falls back to "ready == observed started" (no delay
/// fabricated). A non-empty pattern that fails to compile is a real blueprint bug: detection is
/// disabled (<see cref="Enabled"/> false) and <see cref="Warning"/> is set for the caller to log once
/// — it is deliberately <em>never</em> silently substituted with the immediate rule, which would
/// misrepresent an operator-authored (but broken) readiness contract as "no readiness needed".
/// </para>
/// </summary>
internal sealed class NativeReadinessMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Regex? _ready;

    /// <summary>True when the raw pattern was empty/whitespace-only — the "no distinct readiness
    /// signal configured" case, distinct from an invalid pattern (see class remarks).</summary>
    public bool IsImmediate { get; }

    /// <summary>Set when a non-empty pattern failed to compile, for the caller to log once.</summary>
    public string? Warning { get; }

    public NativeReadinessMatcher(string pattern)
    {
        IsImmediate = string.IsNullOrWhiteSpace(pattern);
        if (IsImmediate)
            return;

        try
        {
            _ready = new Regex(pattern, RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException ex)
        {
            Warning = $"startup_success_regex is not a valid .NET regex — readiness detection disabled: {ex.Message}";
        }
    }

    /// <summary>True when a valid non-empty pattern compiled — pattern-based readiness is armed.</summary>
    public bool Enabled => _ready is not null;

    /// <summary>
    /// Test ONE already-read line. Never throws — a regex match timeout (possible ReDoS pattern) is
    /// treated as a non-match, same as <see cref="NativeLogMatcher"/>'s per-line timeout handling.
    /// </summary>
    public bool IsMatch(string line)
    {
        if (_ready is null || string.IsNullOrEmpty(line))
            return false;
        try
        {
            return _ready.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// One-shot whole-file scan for the late-attach gotcha: an instance that was already past its
    /// ready line by the time this pattern was first evaluated (a daemon hot-swap / restart mid-boot,
    /// or attaching to an already-running instance). Mirrors the bash reference's whole-file
    /// <c>grep -q "$ready_pattern" "$instance_log_file"</c> (<c>watchers.logs.sh</c>
    /// <c>__logic_test_log_pattern</c>): read the entire current file content once and test every
    /// line, independent of any <see cref="EventChannelTail"/> — this deliberately does NOT go through
    /// a tail's offset/inode bookkeeping, so it can never perturb the tail's own resume position.
    /// Best-effort: a missing/unreadable file yields false (retried on the next start edge), never
    /// throws.
    /// </summary>
    public bool MatchesExistingContent(string logPath)
    {
        if (_ready is null || string.IsNullOrEmpty(logPath))
            return false;

        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (IsMatch(line))
                    return true;
            }
        }
        catch
        {
            // absent / unreadable — honest false; a later start edge retries.
        }
        return false;
    }
}
