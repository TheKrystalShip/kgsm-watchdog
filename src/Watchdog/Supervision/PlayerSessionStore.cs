using System.Collections.Concurrent;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Thread-safe, DI-singleton store for per-instance player session maps. Wraps the per-instance
/// <see cref="PlayerSessionMap"/> instances so the native ingester can write and the control
/// surface can read concurrently. Each instance gets its own map, created lazily on first join.
/// <para>
/// It is also where a presence event's identity is completed, and so where every producer that
/// correlates sessions gets the same answer. Two mechanisms, one rule — a field the game's line left
/// blank is filled from something that server itself reported, and a field it carried is never
/// touched: <see cref="PlayerSessionMap"/> merges a leave against the join it was paired with, and
/// <see cref="PlayerNameStore"/> carries an account's display name forward from the last session it
/// was reported in. The second exists because a join is emitted before its leave has been read, so a
/// game that names the player only on disconnect (Necesse) would otherwise announce every arrival as
/// a bare account id no matter how many times that person has played there.
/// </para>
/// </summary>
/// <remarks>
/// The per-instance maps are still non-thread-safe (one writer — the ingester's poll loop), but
/// the outer dictionary and the per-instance accessors are locked so the HTTP endpoint can safely
/// read a snapshot while the ingester writes. Performance is not critical: reads are on-demand
/// (control surface queries), writes are once per matched log line.
/// </remarks>
internal sealed class PlayerSessionStore(PlayerNameStore names)
{
    private readonly ConcurrentDictionary<string, PlayerSessionMap> _maps = new(StringComparer.Ordinal);

    /// <summary>
    /// What a join resolved to: whether it is a new session, and the name to announce it under.
    /// </summary>
    /// <param name="Accepted"><see langword="false"/> when the session key was already tracked (a
    /// doubled join line — dedup), and the caller must emit nothing.</param>
    /// <param name="Name">The name to emit: the one the join line carried, or — only where it carried
    /// none — the one this instance last reported for the same account id. Null when neither exists,
    /// which stays an honest missing field.</param>
    internal readonly record struct JoinOutcome(bool Accepted, string? Name);

    /// <summary>
    /// Record a player join for the given instance, completing the identity from
    /// <see cref="PlayerNameStore"/> where the line left the name blank. Creates the per-instance map
    /// lazily on first use.
    /// </summary>
    public JoinOutcome Join(string instanceName, string sessionKey, string? id, string? name, string? addr)
    {
        // Fill only a blank. A name on the line is what the server said about THIS connection and
        // always wins over anything remembered — the same join-wins rule PlayerSessionMap.Leave
        // applies within a session, here applied across them.
        if (string.IsNullOrWhiteSpace(name))
            name = names.Resolve(instanceName, id);
        else
            names.Learn(instanceName, id, name);

        var map = _maps.GetOrAdd(instanceName, static _ => new PlayerSessionMap());
        lock (map)
        {
            return new JoinOutcome(map.Join(sessionKey, id, name, addr), name);
        }
    }

    /// <summary>
    /// Record a player leave for the given instance. Returns the resolved session identity on
    /// a map hit, an honest fallback if the leave line carried identity, or null (skip — never
    /// fabricate). Creates the per-instance map lazily on first use.
    /// <para>
    /// The resolved pairing is what <see cref="PlayerNameStore"/> learns from, so a game that names
    /// the player only on disconnect teaches the index here and is answered on the account's next
    /// join. It learns from the RESOLVED session rather than the raw line so a game that names the
    /// player only on connect is remembered too.
    /// </para>
    /// </summary>
    public PlayerSessionMap.Session? Leave(string instanceName, string sessionKey, string? id, string? name, string? addr)
    {
        var map = _maps.GetOrAdd(instanceName, static _ => new PlayerSessionMap());
        PlayerSessionMap.Session? resolved;
        lock (map)
        {
            resolved = map.Leave(sessionKey, id, name, addr);
        }

        if (resolved is { } session)
            names.Learn(instanceName, session.Id, session.Name);

        return resolved;
    }

    /// <summary>
    /// Clear all sessions for an instance. Called from the two edges that end every session at once:
    /// the instance's process ending (<c>InstanceSupervisor.ForgetPlayerSessions</c> — a stop, a
    /// crash, or a clean exit) and the log rolling to a fresh inode (a new server session).
    /// </summary>
    public void Reset(string instanceName)
    {
        if (_maps.TryGetValue(instanceName, out var map))
        {
            lock (map)
            {
                map.Reset();
            }
        }
    }

    /// <summary>
    /// Re-seed one instance's map from a hot-swap handoff, replacing whatever it holds. Emits nothing and
    /// reports no dedup verdict — see <see cref="PlayerSessionMap.Restore"/>.
    /// </summary>
    public void Restore(string instanceName, IEnumerable<(string SessionKey, string? Id, string? Name, string? Addr)> sessions)
    {
        var map = _maps.GetOrAdd(instanceName, static _ => new PlayerSessionMap());
        lock (map)
        {
            foreach (var s in sessions)
                map.Restore(s.SessionKey, new PlayerSessionMap.Session(s.Id, s.Name, s.Addr));
        }
    }

    /// <summary>
    /// Drop an instance's map entirely — called when the instance is deregistered (uninstalled), which
    /// is the one edge where it is not merely empty but gone. <see cref="Reset"/> leaves the entry
    /// behind, and an instance that no longer exists reporting an empty player list is a claim about
    /// something that is not there.
    /// </summary>
    public void Forget(string instanceName)
    {
        _maps.TryRemove(instanceName, out _);

        // The remembered names go with it. This is the one edge that justifies dropping them: an
        // instance that no longer exists has no next join to name, and a later instance reusing the
        // name is a different server. A mere stop must never come here — spanning stops is what the
        // index is for.
        names.Forget(instanceName);
    }

    /// <summary>
    /// Whether this instance currently has any tracked session. Lets a caller skip the work — and the
    /// log line — for the overwhelmingly common case of an instance nobody was connected to.
    /// </summary>
    public bool HasSessions(string instanceName)
    {
        if (!_maps.TryGetValue(instanceName, out var map))
            return false;

        lock (map)
        {
            return map.Count > 0;
        }
    }

    /// <summary>
    /// Get a snapshot of all currently tracked sessions for one instance. Returns an empty array
    /// if the instance has no tracked sessions. The returned array is a point-in-time copy.
    /// </summary>
    public IReadOnlyList<PlayerSessionMap.Session> GetSessions(string instanceName)
    {
        if (!_maps.TryGetValue(instanceName, out var map))
            return [];

        lock (map)
        {
            return map.Snapshot();
        }
    }

    /// <summary>
    /// Get a snapshot of all currently tracked sessions across all instances. Returns a
    /// dictionary keyed by instance name. Point-in-time copy.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<PlayerSessionMap.Session>> GetAllSessions()
    {
        var result = new Dictionary<string, IReadOnlyList<PlayerSessionMap.Session>>(StringComparer.Ordinal);
        foreach (var kvp in _maps)
        {
            lock (kvp.Value)
            {
                result[kvp.Key] = kvp.Value.Snapshot();
            }
        }
        return result;
    }

    /// <summary>
    /// Get a snapshot of all currently tracked sessions with their session keys, across all
    /// instances. Used by the control surface to build the <c>GET /players</c> response.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, PlayerSessionMap.Session>>> GetAllSessionsWithKeys()
    {
        var result = new Dictionary<string, IReadOnlyList<KeyValuePair<string, PlayerSessionMap.Session>>>(StringComparer.Ordinal);
        foreach (var kvp in _maps)
        {
            lock (kvp.Value)
            {
                result[kvp.Key] = kvp.Value.SnapshotEntries();
            }
        }
        return result;
    }
}
