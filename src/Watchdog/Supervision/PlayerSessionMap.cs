namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The per-instance, in-memory correlation aid the player-presence contract (§4) calls for: a
/// transient <c>sessionKey → {id, name, addr}</c> map that turns a stream of independently-matched
/// join/leave log lines into correctly-paired presence events. It is the <b>single mechanism</b> for
/// three distinct problems the four validated games all exhibit in some combination:
/// <list type="bullet">
/// <item>
/// <b>Correlation</b> — a game's join and leave lines rarely carry the same fields. Usually the leave
/// is the poorer of the two, down to a bare token (romestead: <c>addr</c>; Valheim/Core Keeper:
/// <c>key</c>) with no display name, and the map supplies what the matching join captured. Necesse
/// runs the other way — its connect line has the SteamID64 and endpoint, its disconnect line has the
/// character name — so the resolution merges the two per field rather than preferring either line
/// wholesale, and a session ends up described by everything the server said about it.
/// </item>
/// <item>
/// <b>Doubled-join dedup</b> — Valheim logs every line twice (a <c>Console:</c>-wrapped form and a bare
/// form). Insert-if-absent on join means the second copy is a no-op.
/// </item>
/// <item>
/// <b>Repeated-leave dedup</b> — Valheim's cleanup burst re-logs the same disconnect up to 6×.
/// Resolve-and-evict means only the first hit emits; the rest find nothing (already evicted) and,
/// having no id/name of their own to fall back on, are honestly skipped.
/// </item>
/// </list>
/// <para>
/// <b>Key precedence (contract §4):</b> <c>sessionKey = first-non-blank(key, addr, id, name)</c> — an
/// opaque <c>key</c> (ZDOID/userid) wins when present (it's the correlation token authored for that
/// game), then a real network <c>addr</c>, then the account-layer <c>id</c>, then the display <c>name</c>
/// as a last resort. The <b>same</b> field must be captured on both join and leave for a game's map
/// entries to ever resolve (an authoring invariant enforced by the blueprint patterns, not this type).
/// </para>
/// <para>
/// <b>Not the roster of record.</b> This is a bounded, per-instance, reset-on-restart detection aid —
/// the durable, queryable roster lives downstream in kgsm-api, keyed on the same <c>sessionKey</c> this
/// type mints. <b>Not thread-safe by design</b> — one map per <see cref="NativePlayerPresenceIngester"/>
/// watch, driven by that instance's single poll loop, exactly like <see cref="EventChannelTail"/>.
/// </para>
/// </summary>
internal sealed class PlayerSessionMap
{
    /// <summary>The identity captured at join time, resolved back out on a matching leave.</summary>
    internal readonly record struct Session(string? Id, string? Name, string? Addr);

    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Clear every tracked session — called when <see cref="EventChannelTail.LastReadResetSession"/>
    /// reports the instance's log rolled to a fresh inode (a new server session): every prior session is
    /// gone with it, so holding onto stale keys could only ever produce a wrong resolution, never a
    /// right one.
    /// </summary>
    public void Reset() => _sessions.Clear();

    /// <summary>How many sessions are tracked right now — an allocation-free alternative to
    /// <see cref="Snapshot"/> for a caller that only needs to know whether the map is empty.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Put a session back exactly as it was, re-seeding this map from a hot-swap handoff. Distinct from
    /// <see cref="Join"/>: nothing was observed, so there is no dedup verdict to report and no event to
    /// emit — this is the predecessor's state being restored, not a player arriving.
    /// </summary>
    public void Restore(string sessionKey, Session session) => _sessions[sessionKey] = session;

    /// <summary>
    /// The contract-frozen precedence: <c>key ?? addr ?? id ?? name</c>, treating a blank/whitespace-only
    /// value as absent. Null only when all four are absent (never happens for a join that passed the
    /// matcher's identity guard; can happen for an under-specified leave line — the caller must handle
    /// that by skipping, not by keying the map on an empty string).
    /// </summary>
    public static string? ComputeSessionKey(string? key, string? addr, string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(key))
            return key;
        if (!string.IsNullOrWhiteSpace(addr))
            return addr;
        if (!string.IsNullOrWhiteSpace(id))
            return id;
        if (!string.IsNullOrWhiteSpace(name))
            return name;
        return null;
    }

    /// <summary>
    /// JOIN: insert-if-absent. Returns <see langword="false"/> when <paramref name="sessionKey"/> is
    /// already tracked — a doubled join line (Valheim logs every line twice) — so the caller skips
    /// emitting a second <c>joined</c> for the same session. Returns <see langword="true"/> (and records
    /// the session) the first time.
    /// </summary>
    public bool Join(string sessionKey, string? id, string? name, string? addr)
    {
        if (_sessions.ContainsKey(sessionKey))
            return false; // already tracked — dedups a doubled join line
        _sessions[sessionKey] = new Session(id, name, addr);
        return true;
    }

    /// <summary>
    /// LEAVE: resolve-and-evict. On a map hit, evicts the session and returns the identity captured at
    /// join <b>merged field-by-field</b> with whatever the leave line carried — this is the
    /// self-describing part (a bare-token leave still emits the name/id/addr seen at join). A repeated
    /// leave (Valheim's burst) then misses (already evicted) and falls to the honest fallback: if the
    /// leave line itself carried an <c>id</c> or <c>name</c>, emit with just that
    /// (<paramref name="addr"/> passed through as-is); otherwise there is nothing to attribute and the
    /// caller must skip — returned as <see langword="null"/>.
    /// <para>
    /// The merge is per-field and join-wins, so it changes nothing for a game whose leave line is the
    /// poorer of the two (every game whose leave carries a bare token: the stored value is present and
    /// is kept). It exists for the reverse case: Necesse's connect line carries the SteamID64 and the
    /// endpoint but no character name, while its disconnect line carries the name. Returning the
    /// stored session verbatim would discard a name the server actually reported and leave the roster
    /// showing a bare SteamID64. A field is filled from the leave line only where the join captured
    /// nothing, so this never overwrites a measured value with a later one — it only stops throwing
    /// one away.
    /// </para>
    /// </summary>
    public Session? Leave(string sessionKey, string? id, string? name, string? addr)
    {
        if (_sessions.Remove(sessionKey, out Session stored))
            return new Session(
                Coalesce(stored.Id, id),
                Coalesce(stored.Name, name),
                Coalesce(stored.Addr, addr));

        if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(name))
            return new Session(id, name, addr); // honest fallback: map missed (e.g. watchdog restart mid-session)

        return null; // nothing to attribute — skip, never fabricate
    }

    /// <summary>
    /// The join-wins rule of <see cref="Leave"/>'s merge: keep what the join captured, and use the
    /// leave line's value only where the join had nothing. Blank is absent, matching how every other
    /// field in this type treats whitespace.
    /// </summary>
    private static string? Coalesce(string? fromJoin, string? fromLeave)
        => string.IsNullOrWhiteSpace(fromJoin) ? fromLeave : fromJoin;

    /// <summary>
    /// Return a point-in-time copy of all tracked sessions. Used by <see cref="PlayerSessionStore"/>
    /// to serve the control surface without holding the lock during serialization.
    /// </summary>
    public IReadOnlyList<Session> Snapshot() => [.. _sessions.Values];

    /// <summary>
    /// Return a point-in-time copy of all tracked sessions with their keys. Used by the control
    /// surface to build the <c>GET /players</c> response.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, Session>> SnapshotEntries() => [.. _sessions];
}
