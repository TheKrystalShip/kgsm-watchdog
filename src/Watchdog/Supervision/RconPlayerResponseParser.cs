using System.Text.RegularExpressions;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Parses the raw text response from a game server's RCON <c>players</c> command into
/// a list of connected player identities. Each game server has its own output format;
/// this parser handles the known formats and falls back to a best-effort extraction.
/// </summary>
/// <remarks>
/// The parser is stateless and pure — constructed once per instance, then called per
/// poll cycle. It does not allocate on the hot path beyond the necessary list/string
/// work (the poll interval is 10+ seconds; allocation is irrelevant).
/// </remarks>
internal sealed class RconPlayerResponseParser
{
    // Project Zomboid format (one of several possible layouts):
    //   -  76561198035585257  PlayerName
    //   -  SteamID  Name (no leading dash in some versions)
    // The regex captures: optional dash/spaces, then a numeric ID (12+ digits for
    // SteamID64), then a name (everything after whitespace until end of line).
    private static readonly Regex PlayerLineRegex = new(
        @"^\s*-?\s*(?<id>\d{12,})\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// A single parsed player entry from an RCON response.
    /// </summary>
    internal readonly record struct PlayerEntry(string Id, string Name);

    /// <summary>
    /// Parses the raw RCON response text into a list of player entries.
    /// Returns an empty list when the response contains no parseable players
    /// (e.g. "Server has 0 players connected").
    /// </summary>
    /// <param name="response">The raw text response from the RCON players command.</param>
    /// <returns>A list of parsed player entries (id + name pairs).</returns>
    internal static IReadOnlyList<PlayerEntry> Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return [];

        var players = new List<PlayerEntry>();
        foreach (Match match in PlayerLineRegex.Matches(response))
        {
            string id = match.Groups["id"].Value;
            string name = match.Groups["name"].Value;

            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                players.Add(new PlayerEntry(id, name));
        }

        return players;
    }
}
