using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the pure NDJSON line → emit-decision translation: that valid join/left lines map to the
/// right dash-form wire event with the right captures, that null/absent fields are honoured (never
/// fabricated), and that the defensive at-least-one-non-null guard, malformed JSON, and unknown types
/// all DROP rather than emit a junk event.
/// </summary>
public sealed class PlayerPresenceParserTests
{
    [Fact]
    public void Joined_with_id_and_name_emits_joined_event()
    {
        var r = PlayerPresenceParser.Parse(
            """{"type":"player_joined","id":"76561198000000000","name":"Alice","ts":"2026-06-19T12:00:00Z"}""");

        Assert.True(r.Emit);
        Assert.Equal(PlayerPresenceParser.EventPlayerJoined, r.EventName);
        Assert.Equal("instance-player-joined", r.EventName); // the literal dash form kgsm normalises
        Assert.Equal("76561198000000000", r.PlayerId);
        Assert.Equal("Alice", r.PlayerName);
    }

    [Fact]
    public void Left_with_id_and_name_emits_left_event()
    {
        var r = PlayerPresenceParser.Parse(
            """{"type":"player_left","id":"u-1","name":"Bob","ts":"2026-06-19T12:05:00Z"}""");

        Assert.True(r.Emit);
        Assert.Equal("instance-player-left", r.EventName);
        Assert.Equal("u-1", r.PlayerId);
        Assert.Equal("Bob", r.PlayerName);
    }

    [Fact]
    public void Null_id_keeps_name_and_emits()
    {
        var r = PlayerPresenceParser.Parse(
            """{"type":"player_joined","id":null,"name":"Carol","ts":"2026-06-19T12:00:00Z"}""");

        Assert.True(r.Emit);
        Assert.Null(r.PlayerId);            // never fabricated
        Assert.Equal("Carol", r.PlayerName);
    }

    [Fact]
    public void Null_name_keeps_id_and_emits()
    {
        var r = PlayerPresenceParser.Parse(
            """{"type":"player_left","id":"steam-42","name":null,"ts":"2026-06-19T12:00:00Z"}""");

        Assert.True(r.Emit);
        Assert.Equal("steam-42", r.PlayerId);
        Assert.Null(r.PlayerName);
    }

    [Fact]
    public void Missing_id_and_name_fields_drop_on_the_guard()
    {
        // Neither field present at all → both null → at-least-one-non-null violated.
        var r = PlayerPresenceParser.Parse("""{"type":"player_joined","ts":"2026-06-19T12:00:00Z"}""");

        Assert.False(r.Emit);
        Assert.Contains("at-least-one-non-null", r.DropReason);
    }

    [Fact]
    public void Both_null_drops_on_the_guard()
    {
        var r = PlayerPresenceParser.Parse(
            """{"type":"player_joined","id":null,"name":null,"ts":"2026-06-19T12:00:00Z"}""");

        Assert.False(r.Emit);
        Assert.Contains("at-least-one-non-null", r.DropReason);
    }

    [Fact]
    public void Whitespace_only_fields_count_as_absent_and_drop()
    {
        var r = PlayerPresenceParser.Parse(
            """{"type":"player_joined","id":"   ","name":"","ts":"2026-06-19T12:00:00Z"}""");

        Assert.False(r.Emit);
        Assert.Contains("at-least-one-non-null", r.DropReason);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"type":"player_joined","id":"a","name":"b" """)] // truncated / unterminated
    [InlineData("{ }{ }")]                                            // trailing garbage after the object
    public void Malformed_json_drops(string line)
    {
        var r = PlayerPresenceParser.Parse(line);

        Assert.False(r.Emit);
        Assert.NotNull(r.DropReason);
    }

    [Theory]
    [InlineData("""{"type":"player_kicked","id":"a","name":"b"}""")] // not in Increment 1
    [InlineData("""{"type":"","id":"a","name":"b"}""")]
    [InlineData("""{"id":"a","name":"b"}""")]                         // no type
    public void Unknown_or_missing_type_drops(string line)
    {
        var r = PlayerPresenceParser.Parse(line);

        Assert.False(r.Emit);
        Assert.Contains("type", r.DropReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Blank_lines_drop_quietly(string line)
    {
        var r = PlayerPresenceParser.Parse(line);

        Assert.False(r.Emit);
        Assert.Equal("blank line", r.DropReason);
    }
}
