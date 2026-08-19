namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// The on-disk shape of the <b>player name index</b> (written by <c>PlayerNameStore</c> to
/// <c>player-names.json</c>, beside <c>desired-state.json</c>, <c>supervision-state.json</c> and
/// <c>run-history.json</c>): the display name each account id was last observed under, per instance.
/// <para>
/// It exists because a game's connect and disconnect lines need not carry the same fields, and some
/// carry the account id on both while naming the player on only one of them. Necesse is the worked
/// case: its connect line has the SteamID64 and the endpoint, its disconnect line has the character
/// name. Within one session <c>PlayerSessionMap</c>'s per-field merge already resolves that, but the
/// join event is emitted before the leave line exists, so the join goes out with a bare id. This
/// index carries the name the server itself reported across sessions, so the next join of the same
/// account is named from the moment it happens.
/// </para>
/// <para>
/// <b>Keyed on the account id only.</b> An id is minted by the game's account layer and identifies one
/// person durably. An address is not: the port is reassigned per connection and the ip is ISP-mutable,
/// so keying on one would attribute a name to whoever next dialled from that endpoint. A game with no
/// account id has nothing here and gains nothing from it — and needs nothing, since a game that reports
/// no id reports the name on both lines.
/// </para>
/// </summary>
internal sealed class PersistedPlayerNames
{
    public int Version { get; set; } = 1;

    /// <summary>Keyed by instance name → its known players, newest-seen first, capped by
    /// <c>PlayerNameStore</c>.</summary>
    public Dictionary<string, List<PlayerNameRecord>> Instances { get; set; } = new();
}

/// <summary>
/// One account id on one instance, and the display name that account was last seen under.
/// </summary>
/// <param name="PlayerId">The game's own account identifier (SteamID64, XUID, UUID — whatever the
/// blueprint's patterns capture as <c>id</c>). The key; never blank.</param>
/// <param name="PlayerName">The display name the server reported for that account. Never blank —
/// an entry with nothing to say is not written, so a lookup that finds a row always yields a name.</param>
/// <param name="LastSeen">When this pairing was last observed, which is what the per-instance cap
/// evicts by: an account nobody has played on in a long time is the one to forget first.</param>
internal sealed record PlayerNameRecord(
    string PlayerId,
    string PlayerName,
    DateTime LastSeen);
