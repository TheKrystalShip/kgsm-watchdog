using System.Collections.Concurrent;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// Thread-safe, DI-singleton store for per-instance player session maps. Wraps the per-instance
/// <see cref="PlayerSessionMap"/> instances so the native ingester can write and the control
/// surface can read concurrently. Each instance gets its own map, created lazily on first join.
/// </summary>
/// <remarks>
/// The per-instance maps are still non-thread-safe (one writer — the ingester's poll loop), but
/// the outer dictionary and the per-instance accessors are locked so the HTTP endpoint can safely
/// read a snapshot while the ingester writes. Performance is not critical: reads are on-demand
/// (control surface queries), writes are once per matched log line.
/// </remarks>
internal sealed class PlayerSessionStore
{
    private readonly ConcurrentDictionary<string, PlayerSessionMap> _maps = new(StringComparer.Ordinal);

    /// <summary>
    /// Record a player join for the given instance. Returns false when the session key is already
    /// tracked (a doubled join line — dedup). Creates the per-instance map lazily on first use.
    /// </summary>
    public bool Join(string instanceName, string sessionKey, string? id, string? name, string? addr)
    {
        var map = _maps.GetOrAdd(instanceName, static _ => new PlayerSessionMap());
        lock (map)
        {
            return map.Join(sessionKey, id, name, addr);
        }
    }

    /// <summary>
    /// Record a player leave for the given instance. Returns the resolved session identity on
    /// a map hit, an honest fallback if the leave line carried identity, or null (skip — never
    /// fabricate). Creates the per-instance map lazily on first use.
    /// </summary>
    public PlayerSessionMap.Session? Leave(string instanceName, string sessionKey, string? id, string? name, string? addr)
    {
        var map = _maps.GetOrAdd(instanceName, static _ => new PlayerSessionMap());
        lock (map)
        {
            return map.Leave(sessionKey, id, name, addr);
        }
    }

    /// <summary>
    /// Clear all sessions for an instance — called when the log rolls to a fresh inode (new server session).
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
