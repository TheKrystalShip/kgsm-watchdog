using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The player name index, and the join it exists to name.
/// <para>
/// The case behind it is Necesse. Its connect line is
/// <c>Client "76561198800558749" with address 82.135.81.20:18661 is connecting…</c> and its disconnect
/// line is <c>Player 76561198800558749 ("gingur") disconnected…</c> — the account id on both, the
/// character name on only the second. <see cref="PlayerSessionMap"/> merges the two per field, so the
/// leave event carries the name, but the join event went out long before the disconnect line existed.
/// Every arrival therefore announced a bare SteamID64 no matter how many times that person had played
/// there, while the roster beside it showed their name.
/// </para>
/// </summary>
[Collection(EnvironmentCollection.Name)] // the store resolves its path from the environment
public sealed class PlayerNameStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "kgsm-wd-names-" + Guid.NewGuid().ToString("N"));

    public PlayerNameStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private WatchdogOptions Options => new() { StateFile = Path.Combine(_dir, "desired-state.json") };

    private PlayerNameStore NewStore() => TestState.PlayerNames(Options);

    private PlayerSessionStore NewSessions() => TestState.Sessions(Options);

    // ── The store itself ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_learned_name_round_trips_through_a_fresh_store()
    {
        NewStore().Learn("necesse", "76561198800558749", "gingur");

        Assert.Equal("gingur", NewStore().Resolve("necesse", "76561198800558749"));
    }

    [Fact]
    public void An_unknown_account_resolves_to_null()
    {
        NewStore().Learn("necesse", "76561198800558749", "gingur");

        Assert.Null(NewStore().Resolve("necesse", "76561198144397568"));
    }

    [Fact]
    public void The_index_is_scoped_per_instance()
    {
        // The same person plays on two servers under two characters. Nothing here says a SteamID64 has
        // one name — only that this instance reported this one.
        var store = NewStore();
        store.Learn("necesse", "76561198800558749", "gingur");
        store.Learn("necesse-2", "76561198800558749", "Ginger");

        Assert.Equal("gingur", store.Resolve("necesse", "76561198800558749"));
        Assert.Equal("Ginger", store.Resolve("necesse-2", "76561198800558749"));
    }

    [Fact]
    public void A_rename_replaces_the_name_rather_than_adding_a_row()
    {
        var store = NewStore();
        store.Learn("necesse", "76561198800558749", "gingur");
        store.Learn("necesse", "76561198800558749", "gingur the second");

        Assert.Equal("gingur the second", store.Resolve("necesse", "76561198800558749"));
    }

    [Theory]
    [InlineData(null, "gingur")]
    [InlineData("", "gingur")]
    [InlineData("   ", "gingur")]
    [InlineData("76561198800558749", null)]
    [InlineData("76561198800558749", "")]
    [InlineData("76561198800558749", "   ")]
    public void Half_a_pairing_is_nothing_to_learn(string? id, string? name)
    {
        var store = NewStore();
        store.Learn("necesse", id, name);

        // Nothing was written, so nothing can be resolved — including by the blank key itself, which
        // must never become a row that answers for every account without one.
        Assert.Null(store.Resolve("necesse", id));
        Assert.Null(store.Resolve("necesse", "76561198800558749"));
    }

    [Fact]
    public void The_per_instance_cap_evicts_the_least_recently_seen()
    {
        var store = NewStore();
        store.Learn("necesse", "regular", "Regular");

        for (int i = 0; i < PlayerNameStore.MaxNamesPerInstance; i++)
            store.Learn("necesse", "visitor-" + i, "Visitor " + i);

        // The regular fell off — they were the oldest by the time the cap was reached.
        Assert.Null(store.Resolve("necesse", "regular"));
        Assert.Equal("Visitor 0", store.Resolve("necesse", "visitor-0"));
    }

    [Fact]
    public void Being_seen_again_keeps_a_regular_ahead_of_the_cap()
    {
        var store = NewStore();
        store.Learn("necesse", "regular", "Regular");

        for (int i = 0; i < PlayerNameStore.MaxNamesPerInstance - 1; i++)
        {
            store.Learn("necesse", "visitor-" + i, "Visitor " + i);
            store.Learn("necesse", "regular", "Regular"); // they keep coming back
        }

        store.Learn("necesse", "one-more", "One More");

        Assert.Equal("Regular", store.Resolve("necesse", "regular"));
    }

    [Fact]
    public void A_corrupt_file_reads_as_empty_rather_than_throwing()
    {
        File.WriteAllText(Path.Combine(_dir, StatePathResolver.PlayerNamesFile), "{ not json");

        var store = NewStore();
        Assert.Null(store.Resolve("necesse", "76561198800558749"));

        // And the next write repairs it.
        store.Learn("necesse", "76561198800558749", "gingur");
        Assert.Equal("gingur", NewStore().Resolve("necesse", "76561198800558749"));
    }

    // ── The join it names ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_necesse_join_is_named_from_the_previous_sessions_leave()
    {
        var sessions = NewSessions();

        // Session one: the connect line has the id and the endpoint, no name.
        PlayerSessionStore.JoinOutcome first = sessions.Join(
            "necesse", "76561198800558749", "76561198800558749", null, "82.135.81.20:18661");
        Assert.True(first.Accepted);
        Assert.Null(first.Name); // nothing known yet — an honest missing field, not a placeholder

        // …and the disconnect line names them.
        PlayerSessionMap.Session? left = sessions.Leave(
            "necesse", "76561198800558749", "76561198800558749", "gingur", null);
        Assert.Equal("gingur", left?.Name);

        // Session two, hours later: the same bare connect line, now named.
        PlayerSessionStore.JoinOutcome second = sessions.Join(
            "necesse", "76561198800558749", "76561198800558749", null, "82.135.81.20:18612");
        Assert.True(second.Accepted);
        Assert.Equal("gingur", second.Name);
    }

    [Fact]
    public void A_name_on_the_line_is_never_replaced_by_a_remembered_one()
    {
        var sessions = NewSessions();
        sessions.Join("necesse", "k1", "76561198800558749", null, null);
        sessions.Leave("necesse", "k1", "76561198800558749", "gingur", null);

        // The server now reports a different name for the same account. What it just said wins over
        // what it said last time — the same join-wins rule the in-session merge applies.
        PlayerSessionStore.JoinOutcome join = sessions.Join(
            "necesse", "k2", "76561198800558749", "gingur the second", null);

        Assert.Equal("gingur the second", join.Name);
        Assert.Equal("gingur the second", NewStore().Resolve("necesse", "76561198800558749"));
    }

    [Fact]
    public void A_game_with_no_account_id_learns_nothing_and_is_unaffected()
    {
        // romestead: the name is the identity and rides on both lines, so there is no id to key on and
        // nothing this index could add. It must not key on the address instead — a port is reassigned
        // per connection and an ip is ISP-mutable, so that would eventually name a stranger.
        var sessions = NewSessions();
        sessions.Join("romestead", "92.31.7.177:50001", null, "Juno", "92.31.7.177:50001");
        sessions.Leave("romestead", "92.31.7.177:50001", null, "Juno", "92.31.7.177:50001");

        PlayerSessionStore.JoinOutcome next = sessions.Join(
            "romestead", "92.31.7.177:50002", null, null, "92.31.7.177:50002");

        Assert.Null(next.Name);
    }

    [Fact]
    public void A_name_carried_only_on_the_connect_line_is_remembered_too()
    {
        // The mirror of Necesse: some games name the player on connect and log a bare token on
        // disconnect. Learning from the RESOLVED session rather than the raw leave line covers both.
        var sessions = NewSessions();
        sessions.Join("stationeers", "76561198035585257", "76561198035585257", "Insanity", null);
        sessions.Leave("stationeers", "76561198035585257", "76561198035585257", null, null);

        Assert.Equal("Insanity", NewStore().Resolve("stationeers", "76561198035585257"));
    }

    [Fact]
    public void Stopping_an_instance_keeps_its_names_but_removing_it_does_not()
    {
        var sessions = NewSessions();
        sessions.Join("necesse", "k1", "76561198800558749", null, null);
        sessions.Leave("necesse", "k1", "76561198800558749", "gingur", null);

        // A stop ends every session at once; spanning that is the whole point of the index.
        sessions.Reset("necesse");
        Assert.Equal("gingur", sessions.Join("necesse", "k2", "76561198800558749", null, null).Name);

        // Deregistering is different: the instance is gone, and a later one reusing the name is a
        // different server.
        sessions.Forget("necesse");
        Assert.Null(sessions.Join("necesse", "k3", "76561198800558749", null, null).Name);
    }
}
