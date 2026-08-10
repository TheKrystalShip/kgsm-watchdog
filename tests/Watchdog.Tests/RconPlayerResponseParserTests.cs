using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The RCON roster parser — one operation applied to whatever pattern a blueprint supplies. There is
/// no per-game branch to test, so these cover the operation and then use real captured output from
/// real games as the evidence that the operation is the right one.
/// </summary>
public sealed class RconPlayerResponseParserTests
{
    // Verbatim from Project Zomboid build 42, and the pattern its blueprint carries.
    private const string ZomboidPattern = @"^-\s*(?<name>\S.*?)\s*$";

    // A columnar roster: an id and a name per line, the shape a Source-style server prints.
    private const string IdAndNamePattern = @"^\s*-?\s*(?<id>\d{12,})\s+(?<name>.+?)\s*$";

    [Fact]
    public void An_empty_response_has_no_players()
    {
        Assert.Empty(RconPlayerResponseParser.Parse("", ZomboidPattern));
        Assert.Empty(RconPlayerResponseParser.Parse("   \n  \n  ", ZomboidPattern));
    }

    [Fact]
    public void An_empty_pattern_yields_no_players_rather_than_guessing()
    {
        // Not the same as a server reporting nobody connected. The caller skips an instance in this
        // state precisely so the two never get conflated.
        Assert.Empty(RconPlayerResponseParser.Parse("Players connected (1): \n-Heisen\n", ""));
        Assert.False(RconPlayerResponseParser.IsValidPattern(""));
    }

    [Fact]
    public void A_pattern_that_does_not_compile_is_reported_rather_than_thrown()
    {
        Assert.False(RconPlayerResponseParser.IsValidPattern("(?<name>"));
        Assert.Empty(RconPlayerResponseParser.Parse("-Heisen\n", "(?<name>"));
    }

    [Fact]
    public void A_pattern_is_matched_per_line_so_prose_around_the_roster_is_not_a_player()
    {
        const string response =
            "Players connected (1): \n" +
            "-Heisen\n" +
            "End of player list.\n" +
            "Server is running normally\n";

        var player = Assert.Single(RconPlayerResponseParser.Parse(response, ZomboidPattern));
        Assert.Equal("Heisen", player.Name);
    }

    [Fact]
    public void A_match_capturing_neither_group_is_not_a_player()
    {
        // Nothing identifies the entry, so no later poll could retire it.
        Assert.Empty(RconPlayerResponseParser.Parse("-Heisen\n", @"^-.*$"));
    }

    // ---- Project Zomboid: names only, captured from a live server --------------------------------

    [Fact]
    public void Zomboid_zero_players()
    {
        // The count in the header is the trap: a parser reading numbers out of prose reports a player
        // here, named after the server's own wording.
        Assert.Empty(RconPlayerResponseParser.Parse("Players connected (0): \n", ZomboidPattern));
    }

    [Fact]
    public void Zomboid_one_player_states_a_name_and_no_id()
    {
        var player = Assert.Single(
            RconPlayerResponseParser.Parse("Players connected (1): \n-Heisen\n", ZomboidPattern));

        Assert.Null(player.Id);
        Assert.Equal("Heisen", player.Name);
        Assert.Equal("Heisen", player.Key);
    }

    [Fact]
    public void Zomboid_several_players_keep_their_order()
    {
        var result = RconPlayerResponseParser.Parse(
            "Players connected (3): \n-Heisen\n-Juno\n-Ketchup\n", ZomboidPattern);

        Assert.Equal(["Heisen", "Juno", "Ketchup"], result.Select(p => p.Name));
        Assert.All(result, p => Assert.Null(p.Id));
    }

    [Fact]
    public void Zomboid_names_may_contain_spaces()
    {
        var player = Assert.Single(
            RconPlayerResponseParser.Parse("Players connected (1): \n-The Great Player\n", ZomboidPattern));

        Assert.Equal("The Great Player", player.Name);
    }

    // ---- A columnar roster: the same parser, a different pattern ---------------------------------

    [Fact]
    public void An_id_bearing_roster_reports_both_fields()
    {
        const string response =
            "Server has 2 players connected:\n" +
            "-  76561198035585257  PlayerOne\n" +
            "-  76561198012345678  Player Two\n" +
            "End of player list.\n";

        var result = RconPlayerResponseParser.Parse(response, IdAndNamePattern);

        Assert.Equal(2, result.Count);
        Assert.Equal("76561198035585257", result[0].Id);
        Assert.Equal("PlayerOne", result[0].Name);
        Assert.Equal("76561198035585257", result[0].Key); // the id identifies it, when stated
        Assert.Equal("Player Two", result[1].Name);
    }

    [Fact]
    public void An_id_bearing_roster_accepts_an_entry_with_no_dash()
    {
        var player = Assert.Single(
            RconPlayerResponseParser.Parse("  76561198035585257  PlayerOne\n", IdAndNamePattern));

        Assert.Equal("76561198035585257", player.Id);
    }

    [Fact]
    public void A_pattern_that_demands_an_id_skips_a_line_without_one()
    {
        // The pattern is the contract: this roster's entries are id-bearing, so a line that is not
        // is the server's own malformed output and is left out rather than read as a name.
        Assert.Empty(RconPlayerResponseParser.Parse("-  12345  ShortId\n", IdAndNamePattern));
    }
}
