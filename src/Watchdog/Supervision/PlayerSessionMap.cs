namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The per-instance, in-memory correlation aid the player-presence contract (§4) calls for: a
/// transient <c>sessionKey → {id, name, addr}</c> map that turns a stream of independently-matched
/// join/leave log lines into correctly-paired presence events. It is the <b>single mechanism</b> for
/// three distinct problems the four validated games all exhibit in some combination:
/// <list type="bullet">
/// <item>
/// <b>Correlation</b> — some games' leave lines carry only a bare token (romestead: <c>addr</c>;
/// Valheim/Core Keeper: <c>key</c>), never the display name. The map remembers what the matching join
/// captured so the leave can still emit a self-describing event.
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
    /// LEAVE: resolve-and-evict. On a map hit, evicts the session and returns what was captured at join
    /// — this is the self-describing part (a bare-token leave still emits the name/id/addr seen at
    /// join). A repeated leave (Valheim's burst) then misses (already evicted) and falls to the honest
    /// fallback: if the leave line itself carried an <c>id</c> or <c>name</c>, emit with just that
    /// (<paramref name="addr"/> passed through as-is); otherwise there is nothing to attribute and the
    /// caller must skip — returned as <see langword="null"/>.
    /// </summary>
    public Session? Leave(string sessionKey, string? id, string? name, string? addr)
    {
        if (_sessions.Remove(sessionKey, out Session stored))
            return stored;

        if (!string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(name))
            return new Session(id, name, addr); // honest fallback: map missed (e.g. watchdog restart mid-session)

        return null; // nothing to attribute — skip, never fabricate
    }

    /// <summary>
    /// Return a point-in-time copy of all tracked sessions. Used by <see cref="PlayerSessionStore"/>
    /// to serve the control surface without holding the lock during serialization.
    /// </summary>
    public IReadOnlyList<Session> Snapshot() => [.. _sessions.Values];
}
