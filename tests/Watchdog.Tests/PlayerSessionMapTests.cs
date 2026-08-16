using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the pure per-instance correlation map in isolation (player-presence contract §4): the
/// <c>key ?? addr ?? id ?? name</c> precedence, insert-if-absent dedup on join, resolve-and-evict on
/// leave (including the honest fallback and the nothing-to-attribute skip), and reset. The full
/// real-log-line game scenarios (matcher + map + tail together) live in
/// <see cref="NativePlayerPresenceIngesterTests"/>.
/// </summary>
public sealed class PlayerSessionMapTests
{
    // ---- ComputeSessionKey precedence -------------------------------------------------------------

    [Fact]
    public void Key_wins_over_addr_id_and_name()
        => Assert.Equal("k", PlayerSessionMap.ComputeSessionKey(key: "k", addr: "a", id: "i", name: "n"));

    [Fact]
    public void Addr_wins_over_id_and_name_when_key_absent()
        => Assert.Equal("a", PlayerSessionMap.ComputeSessionKey(key: null, addr: "a", id: "i", name: "n"));

    [Fact]
    public void Id_wins_over_name_when_key_and_addr_absent()
        => Assert.Equal("i", PlayerSessionMap.ComputeSessionKey(key: null, addr: null, id: "i", name: "n"));

    [Fact]
    public void Name_is_the_last_resort()
        => Assert.Equal("n", PlayerSessionMap.ComputeSessionKey(key: null, addr: null, id: null, name: "n"));

    [Fact]
    public void Whitespace_only_fields_count_as_absent_in_the_precedence()
        => Assert.Equal("i", PlayerSessionMap.ComputeSessionKey(key: "   ", addr: "", id: "i", name: "n"));

    [Fact]
    public void All_absent_yields_null()
        => Assert.Null(PlayerSessionMap.ComputeSessionKey(null, null, null, null));

    // ---- Join: insert-if-absent -------------------------------------------------------------------

    [Fact]
    public void Join_inserts_the_first_time()
    {
        var map = new PlayerSessionMap();
        Assert.True(map.Join("K", "id-1", "Alice", null));
    }

    [Fact]
    public void Join_is_a_noop_dedup_the_second_time_doubled_line()
    {
        // Valheim logs every line twice — the second copy of the same join must not re-insert.
        var map = new PlayerSessionMap();
        Assert.True(map.Join("K", "id-1", "Alice", null));
        Assert.False(map.Join("K", "id-1", "Alice", null)); // doubled line — deduped
    }

    // ---- Leave: resolve-and-evict ------------------------------------------------------------------

    [Fact]
    public void Leave_resolves_the_identity_captured_at_join_bare_token_leave()
    {
        // romestead-shaped: the leave line itself carries only addr — the map supplies the name.
        var map = new PlayerSessionMap();
        map.Join("86.191.216.57:58845", id: null, name: "Aelia", addr: "86.191.216.57:58845");

        var resolved = map.Leave("86.191.216.57:58845", id: null, name: null, addr: "86.191.216.57:58845");

        Assert.NotNull(resolved);
        Assert.Equal("Aelia", resolved!.Value.Name);
        Assert.Equal("86.191.216.57:58845", resolved.Value.Addr);
        Assert.Null(resolved.Value.Id);
    }

    [Fact]
    public void Leave_evicts_so_a_repeated_leave_burst_only_resolves_once()
    {
        // Valheim's 6x repeated "destroying abandoned zdo" cleanup burst — only the first resolves.
        var map = new PlayerSessionMap();
        map.Join("651023867", id: null, name: "Test", addr: null);

        var first = map.Leave("651023867", id: null, name: null, addr: null);
        Assert.NotNull(first);
        Assert.Equal("Test", first!.Value.Name);

        // The next 5 repeats: already evicted, and the leave line itself carries no id/name → skip.
        for (int i = 0; i < 5; i++)
            Assert.Null(map.Leave("651023867", id: null, name: null, addr: null));
    }

    [Fact]
    public void Leave_takes_the_name_from_the_leave_line_when_the_join_had_none()
    {
        // Necesse-shaped, and the reverse of the bare-token case above: the connect line carries the
        // SteamID64 and the endpoint but no character name, and the disconnect line is the only place
        // the name appears. Returning the stored session verbatim would throw away a name the server
        // actually reported and leave the roster showing a bare account id.
        var map = new PlayerSessionMap();
        map.Join("76561198144397568", id: "76561198144397568", name: null, addr: "95.19.50.122:61042");

        var resolved = map.Leave("76561198144397568", id: "76561198144397568", name: "Heisen", addr: null);

        Assert.NotNull(resolved);
        Assert.Equal("Heisen", resolved!.Value.Name);
        // The join's fields survive: the endpoint is on the connect line only.
        Assert.Equal("76561198144397568", resolved.Value.Id);
        Assert.Equal("95.19.50.122:61042", resolved.Value.Addr);
    }

    [Fact]
    public void Leave_keeps_the_join_value_when_both_lines_carry_the_field()
    {
        // Join-wins, so the merge cannot rewrite an identity mid-session: a leave line disagreeing with
        // the join (a renamed character, a recycled key) does not overwrite what was measured at join.
        var map = new PlayerSessionMap();
        map.Join("K", id: "id-join", name: "Aelia", addr: "1.1.1.1:100");

        var resolved = map.Leave("K", id: "id-leave", name: "Someone Else", addr: "2.2.2.2:200");

        Assert.NotNull(resolved);
        Assert.Equal("id-join", resolved!.Value.Id);
        Assert.Equal("Aelia", resolved.Value.Name);
        Assert.Equal("1.1.1.1:100", resolved.Value.Addr);
    }

    [Fact]
    public void Leave_treats_a_whitespace_only_join_field_as_absent_when_merging()
    {
        // Blank is absent everywhere else in this type; the merge must agree, or a pattern that matched
        // an empty group would pin the field to "" and still lose the leave line's value.
        var map = new PlayerSessionMap();
        map.Join("K", id: "the-id", name: "   ", addr: null);

        var resolved = map.Leave("K", id: null, name: "Heisen", addr: null);

        Assert.NotNull(resolved);
        Assert.Equal("Heisen", resolved!.Value.Name);
        Assert.Equal("the-id", resolved.Value.Id);
    }

    [Fact]
    public void Leave_falls_back_to_the_lines_own_identity_on_a_map_miss()
    {
        // Honest fallback: the watchdog restarted mid-session (or otherwise never saw the join), but the
        // leave line is self-identifying (stationeers-shaped) — emit with just what the line carries.
        var map = new PlayerSessionMap();

        var resolved = map.Leave("76561198144397568", id: "76561198144397568", name: "Heisen", addr: null);

        Assert.NotNull(resolved);
        Assert.Equal("76561198144397568", resolved!.Value.Id);
        Assert.Equal("Heisen", resolved.Value.Name);
    }

    [Fact]
    public void Leave_skips_when_the_map_misses_and_the_line_has_no_identity_either()
    {
        // A bare-token leave (no id/name) whose join was never seen (or whose session already reset) —
        // nothing to attribute; must not fabricate an all-null presence event.
        var map = new PlayerSessionMap();

        Assert.Null(map.Leave("some-key", id: null, name: null, addr: null));
    }

    [Fact]
    public void Self_identifying_join_and_leave_still_go_through_the_map_stationeers_shaped()
    {
        var map = new PlayerSessionMap();
        map.Join("76561198144397568", id: "76561198144397568", name: "Heisen", addr: null);

        var resolved = map.Leave("76561198144397568", id: "76561198144397568", name: "Heisen", addr: null);

        Assert.NotNull(resolved);
        Assert.Equal("Heisen", resolved!.Value.Name);
        Assert.Equal("76561198144397568", resolved.Value.Id);
    }

    [Fact]
    public void Co_nat_sessions_on_different_ports_stay_distinct()
    {
        // Two players behind the same NAT gateway share an IP but not a port — each is its own session.
        var map = new PlayerSessionMap();
        map.Join("86.191.216.57:58845", id: null, name: "Aelia", addr: "86.191.216.57:58845");
        map.Join("86.191.216.57:53376", id: null, name: "Brutus", addr: "86.191.216.57:53376");

        var left1 = map.Leave("86.191.216.57:58845", null, null, "86.191.216.57:58845");
        var left2 = map.Leave("86.191.216.57:53376", null, null, "86.191.216.57:53376");

        Assert.Equal("Aelia", left1!.Value.Name);
        Assert.Equal("Brutus", left2!.Value.Name);
    }

    // ---- Reset --------------------------------------------------------------------------------------

    [Fact]
    public void Reset_clears_every_tracked_session()
    {
        var map = new PlayerSessionMap();
        map.Join("K", "id-1", "Alice", null);

        map.Reset();

        // Post-reset: a leave for the pre-reset session finds nothing, and (bare-token, no fallback
        // identity) is honestly skipped — never a stale/fabricated emit.
        Assert.Null(map.Leave("K", id: null, name: null, addr: null));
        // And a join for the same key is treated as fresh (not deduped) since the prior entry is gone.
        Assert.True(map.Join("K", "id-1", "Alice", null));
    }
}
