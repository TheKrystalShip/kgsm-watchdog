using TheKrystalShip.KGSM.Watchdog.Supervision;
using Xunit;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Tests for the RconPlayerResponseParser — the pure parser that extracts player
/// identities from raw RCON command output. No network I/O, no mock servers.
/// </summary>
public sealed class RconPlayerResponseParserTests
{
    [Fact]
    public void Parse_empty_string_returns_empty()
    {
        var result = RconPlayerResponseParser.Parse("");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_whitespace_only_returns_empty()
    {
        var result = RconPlayerResponseParser.Parse("   \n  \n  ");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_single_player_line()
    {
        const string response = "-  76561198035585257  PlayerOne\n";
        var result = RconPlayerResponseParser.Parse(response);

        Assert.Single(result);
        Assert.Equal("76561198035585257", result[0].Id);
        Assert.Equal("PlayerOne", result[0].Name);
    }

    [Fact]
    public void Parse_multiple_player_lines()
    {
        const string response =
            "-  76561198035585257  PlayerOne\n" +
            "-  76561198012345678  PlayerTwo\n" +
            "-  76561198099999999  PlayerThree\n";
        var result = RconPlayerResponseParser.Parse(response);

        Assert.Equal(3, result.Count);
        Assert.Equal("76561198035585257", result[0].Id);
        Assert.Equal("PlayerOne", result[0].Name);
        Assert.Equal("76561198012345678", result[1].Id);
        Assert.Equal("PlayerTwo", result[1].Name);
        Assert.Equal("76561198099999999", result[2].Id);
        Assert.Equal("PlayerThree", result[2].Name);
    }

    [Fact]
    public void Parse_player_names_with_spaces()
    {
        const string response = "-  76561198035585257  The Great Player\n";
        var result = RconPlayerResponseParser.Parse(response);

        Assert.Single(result);
        Assert.Equal("76561198035585257", result[0].Id);
        Assert.Equal("The Great Player", result[0].Name);
    }

    [Fact]
    public void Parse_header_and_footer_lines_are_ignored()
    {
        const string response =
            "Server has 2 players connected:\n" +
            "-  76561198035585257  PlayerOne\n" +
            "-  76561198012345678  PlayerTwo\n" +
            "End of player list.\n";
        var result = RconPlayerResponseParser.Parse(response);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Parse_no_dashes_format()
    {
        const string response = "  76561198035585257  PlayerOne\n";
        var result = RconPlayerResponseParser.Parse(response);

        Assert.Single(result);
        Assert.Equal("76561198035585257", result[0].Id);
        Assert.Equal("PlayerOne", result[0].Name);
    }

    [Fact]
    public void Parse_zero_players_message()
    {
        const string response = "Server has 0 players connected\n";
        var result = RconPlayerResponseParser.Parse(response);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_short_ids_are_skipped()
    {
        const string response = "-  12345  ShortId\n";
        var result = RconPlayerResponseParser.Parse(response);

        Assert.Empty(result);
    }
}
