using System.Text.RegularExpressions;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Reads connected players out of the raw text a game server answers its RCON players command with,
/// using the pattern that instance's blueprint supplies. The parser holds no knowledge of any game:
/// one game prints <c>-Name</c> under a header, another an id and a name in columns, and both are
/// the same operation applied to a different pattern.
/// </summary>
/// <remarks>
/// A pattern is matched against each line on its own — anchors mean the line, not the response, and
/// a line that does not match is prose the server printed around the roster. Both capture groups are
/// optional: a game that states an id supplies <c>id</c>, one that states only a name supplies
/// <c>name</c>, and an entry carries whichever the server actually stated.
/// <para>
/// Compiled patterns are cached by pattern text. A poll happens every ten seconds or so per instance,
/// while compilation is the expensive part, and the set of distinct patterns on a host is bounded by
/// the number of blueprints installed.
/// </para>
/// </remarks>
internal static class RconPlayerResponseParser
{
    private static readonly Dictionary<string, Regex?> _compiled = new(StringComparer.Ordinal);
    private static readonly Lock _gate = new();

    /// <summary>
    /// A single parsed player entry from an RCON response. Both fields reflect what the server
    /// stated: an id is absent for a game whose roster carries names only, and is never filled in
    /// from the name to give every entry the same shape.
    /// </summary>
    internal readonly record struct PlayerEntry(string? Id, string? Name)
    {
        /// <summary>
        /// What identifies this entry between polls — the id when the server states one, otherwise
        /// the name. Empty when the pattern captured neither, which makes the entry unusable.
        /// </summary>
        public string Key => string.IsNullOrEmpty(Id) ? Name ?? string.Empty : Id;
    }

    /// <summary>
    /// Reads the player entries out of an RCON response.
    /// </summary>
    /// <param name="response">The raw text the server answered the players command with.</param>
    /// <param name="pattern">
    /// The blueprint's per-line pattern, with optional named groups <c>id</c> and <c>name</c>. An
    /// empty pattern yields no players: the response cannot be read, which is not the same as a
    /// server reporting nobody connected, and inventing the difference is what a roster must never do.
    /// </param>
    /// <returns>One entry per line the pattern matched, in the order the server listed them.</returns>
    internal static IReadOnlyList<PlayerEntry> Parse(string response, string pattern)
    {
        if (string.IsNullOrWhiteSpace(response) || string.IsNullOrWhiteSpace(pattern))
            return [];

        Regex? regex = GetOrCompile(pattern);
        if (regex is null)
            return [];

        var players = new List<PlayerEntry>();
        foreach (string line in response.Split('\n'))
        {
            Match match = regex.Match(line);
            if (!match.Success)
                continue;

            string? id = Captured(match, "id");
            string? name = Captured(match, "name");

            // A match that captured neither names nobody. Keeping it would put an entry in the
            // roster that nothing can identify, and that no later poll could ever retire.
            if (id is null && name is null)
                continue;

            players.Add(new PlayerEntry(id, name));
        }

        return players;
    }

    private static string? Captured(Match match, string group)
    {
        Group g = match.Groups[group];
        return g.Success && !string.IsNullOrWhiteSpace(g.Value) ? g.Value.Trim() : null;
    }

    /// <summary>
    /// The compiled form of a blueprint pattern, or null when it does not compile. A bad pattern is
    /// cached as null so a malformed blueprint is reported once rather than on every poll, and costs
    /// the instance its RCON presence instead of the poll pass it appears in.
    /// </summary>
    private static Regex? GetOrCompile(string pattern)
    {
        lock (_gate)
        {
            if (_compiled.TryGetValue(pattern, out Regex? cached))
                return cached;

            Regex? regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                regex = null;
            }

            _compiled[pattern] = regex;
            return regex;
        }
    }

    /// <summary>Whether a blueprint pattern compiles, for reporting a bad one at its source.</summary>
    internal static bool IsValidPattern(string pattern) =>
        !string.IsNullOrWhiteSpace(pattern) && GetOrCompile(pattern) is not null;
}
