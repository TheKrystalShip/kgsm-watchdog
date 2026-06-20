using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the pure native log matcher — the .NET analog of the container in-image shim's matching step.
/// Pins: named-group capture (id/name, either optional), the at-least-one-non-null drop, an empty pattern
/// disabling that detection, an invalid pattern disabling + warning (never throwing), joined-before-left
/// precedence, and that a non-matching line is a SILENT ignore (not a loggable drop).
/// </summary>
public sealed class NativeLogMatcherTests
{
    // Factorio-shaped patterns with both named groups, authored here for the test (not a real capture).
    private const string Joined = @"\[JOIN\] (?<name>\S+) \((?<id>\d+)\) joined";
    private const string Left = @"\[LEAVE\] (?<name>\S+) \((?<id>\d+)\) left";

    [Fact]
    public void Join_with_id_and_name_emits_both()
    {
        var m = new NativeLogMatcher(Joined, Left);
        var r = m.Match("2026-06-20 12:00:00 [JOIN] Alice (76561198000000000) joined the game");

        Assert.True(r.Emit);
        Assert.Equal(PlayerPresenceParser.EventPlayerJoined, r.EventName);
        Assert.Equal("76561198000000000", r.PlayerId);
        Assert.Equal("Alice", r.PlayerName);
    }

    [Fact]
    public void Left_matches_after_joined_and_emits_the_left_event()
    {
        var m = new NativeLogMatcher(Joined, Left);
        var r = m.Match("2026-06-20 12:05:00 [LEAVE] Bob (42) left the game");

        Assert.True(r.Emit);
        Assert.Equal(PlayerPresenceParser.EventPlayerLeft, r.EventName);
        Assert.Equal("42", r.PlayerId);
        Assert.Equal("Bob", r.PlayerName);
    }

    [Fact]
    public void Name_only_pattern_emits_name_with_null_id()
    {
        // A source that only exposes a display name (e.g. Minecraft) — id stays honestly null.
        var m = new NativeLogMatcher(@"(?<name>\w+) joined the game", "");
        var r = m.Match("Steve joined the game");

        Assert.True(r.Emit);
        Assert.Equal("Steve", r.PlayerName);
        Assert.Null(r.PlayerId);
    }

    [Fact]
    public void Id_only_pattern_emits_id_with_null_name()
    {
        var m = new NativeLogMatcher(@"client authenticated steamid (?<id>\d+)", "");
        var r = m.Match("client authenticated steamid 76561198000000001");

        Assert.True(r.Emit);
        Assert.Equal("76561198000000001", r.PlayerId);
        Assert.Null(r.PlayerName);
    }

    [Fact]
    public void Match_with_no_captured_id_or_name_is_dropped_with_a_reason()
    {
        // The pattern matches but has no id/name group → meaningless presence event → drop (+reason to log).
        var m = new NativeLogMatcher("a player joined", "");
        var r = m.Match("a player joined");

        Assert.False(r.Emit);
        Assert.NotNull(r.DropReason);
        Assert.Contains("at-least-one-non-null", r.DropReason);
    }

    [Fact]
    public void Non_matching_line_is_a_silent_ignore_not_a_drop()
    {
        var m = new NativeLogMatcher(Joined, Left);
        var r = m.Match("2026-06-20 12:00:00 [INFO] Saving the map...");

        Assert.False(r.Emit);
        Assert.Null(r.DropReason); // the common case: not a presence line → never logged
    }

    [Fact]
    public void Empty_patterns_disable_detection()
    {
        var m = new NativeLogMatcher("", "");
        Assert.False(m.Enabled);
        Assert.Empty(m.Warnings);
        Assert.False(m.Match("[JOIN] Alice (1) joined").Emit); // nothing compiled → nothing matches
    }

    [Fact]
    public void Invalid_pattern_is_disabled_and_warned_never_throws()
    {
        // An unbalanced group is an invalid .NET regex — that detection disables + records a warning;
        // the OTHER (valid) pattern still works, so the matcher stays enabled.
        var m = new NativeLogMatcher("(?<name>unterminated", Left);

        Assert.True(m.Enabled);                       // the valid 'left' pattern still compiled
        Assert.Single(m.Warnings);
        Assert.Contains("player_joined_regex", m.Warnings[0]);

        Assert.False(m.Match("[JOIN] Alice (1) joined").Emit);  // joined disabled
        Assert.True(m.Match("[LEAVE] Bob (2) left").Emit);      // left still matches
    }
}
