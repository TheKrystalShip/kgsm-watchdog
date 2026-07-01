using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the pure native log matcher — the .NET analog of the container in-image shim's matching step.
/// Pins: named-group capture (id/name, either optional), the at-least-one-non-null drop, an empty pattern
/// disabling that detection, an invalid pattern disabling + warning (never throwing), joined-before-left
/// precedence, and that a non-matching line is a SILENT ignore (not a loggable drop). Also covers the
/// player-presence contract §4 additions: the <c>addr</c>/<c>key</c>/<c>reason</c> groups, the relaxed
/// join guard (id||name||addr — <c>key</c> alone doesn't count), and that a leave has no such guard.
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

    // ---- contract §4 additions: addr/key/reason groups + the relaxed join guard --------------------

    [Fact]
    public void Addr_only_join_satisfies_the_relaxed_identity_guard()
    {
        // romestead-shaped: no id, no key, just a real ip:port — addr alone must satisfy the guard.
        var m = new NativeLogMatcher(@"\(Peer \d+ - (?<addr>[\d.]+:\d+)\) logged in", "");
        var r = m.Match("Character 'Aelia' (Peer 0 - 86.191.216.57:58845) logged in with external id ''");

        Assert.True(r.Emit);
        Assert.Equal("86.191.216.57:58845", r.PlayerAddr);
        Assert.Null(r.PlayerId);
        Assert.Null(r.PlayerName);
    }

    [Fact]
    public void Key_only_join_still_fails_the_guard_key_is_not_an_identity()
    {
        // An opaque correlation token with no id/name/addr alongside it is not a roster-worthy identity —
        // the guard only recognizes id/name/addr, `key` is deliberately excluded.
        var m = new NativeLogMatcher(@"session (?<key>\d+) opened", "");
        var r = m.Match("session 12345 opened");

        Assert.False(r.Emit);
        Assert.NotNull(r.DropReason);
        Assert.Contains("at-least-one-non-null", r.DropReason);
    }

    [Fact]
    public void Join_captures_key_alongside_name_valheim_shaped()
    {
        var m = new NativeLogMatcher(@"Got character ZDOID from (?<name>.+?) : (?<key>\d+):\d+", "");
        var r = m.Match("Got character ZDOID from Test : 651023867:1");

        Assert.True(r.Emit);
        Assert.Equal("Test", r.PlayerName);
        Assert.Equal("651023867", r.Key);
        Assert.Null(r.PlayerId);
        Assert.Null(r.PlayerAddr);
    }

    [Fact]
    public void Leave_with_only_addr_is_not_guarded_the_map_resolves_it()
    {
        // romestead's leave line carries nothing but the endpoint — the matcher must NOT drop this (no
        // identity guard on leave); PlayerSessionMap is what recovers the name.
        var m = new NativeLogMatcher("", @"Peer (?<addr>[\d.]+:\d+) disconnected");
        var r = m.Match("Peer 86.191.216.57:58845 disconnected - RemoteConnectionClose");

        Assert.True(r.Emit);
        Assert.Equal(PlayerPresenceParser.EventPlayerLeft, r.EventName);
        Assert.Equal("86.191.216.57:58845", r.PlayerAddr);
        Assert.Null(r.PlayerId);
        Assert.Null(r.PlayerName);
    }

    [Fact]
    public void Leave_captures_reason_corekeeper_shaped()
    {
        var m = new NativeLogMatcher("", @"Disconnected from userid:(?<key>\d+) with reason (?<reason>\S+)");
        var r = m.Match("Disconnected from userid:3801603394 with reason App_Min");

        Assert.True(r.Emit);
        Assert.Equal("3801603394", r.Key);
        Assert.Equal("App_Min", r.Reason);
    }
}
