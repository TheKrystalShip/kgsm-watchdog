using System.Text.Json;

using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The durable per-instance <c>accountId → display name</c> index, persisted to
/// <c>player-names.json</c> in the state directory (<see cref="StatePathResolver"/>).
/// <para>
/// <b>What it is for.</b> <see cref="PlayerSessionMap"/> merges a join and a leave per field, so a
/// session ends up described by everything the server said about it — but only once both lines have
/// been read. The join event is emitted the moment the connect line appears, which is before the
/// disconnect line exists, so a game that names the player only on disconnect (Necesse) emits a join
/// carrying a bare account id and a leave carrying the name. This index remembers the pairing the
/// server itself reported, so the account's next join is named as it happens.
/// </para>
/// <para>
/// <b>The same rule as the in-session merge, one session wider.</b> A remembered name fills a field
/// the connect line left blank and never replaces one it carried, so a value the server just reported
/// is never overwritten by an older one — identical to <see cref="PlayerSessionMap.Leave"/>'s
/// join-wins coalesce, applied across sessions rather than within one. Nothing is invented: every
/// name here was printed by that server, for that account id.
/// </para>
/// <para>
/// <b>Account ids only.</b> <see cref="Learn"/> and <see cref="Resolve"/> both refuse a blank id, and
/// no other field is ever a key. An address is deliberately not one: its port is reassigned per
/// connection and its ip is ISP-mutable, so keying on one would eventually put a name on whoever
/// next dialled from that endpoint. A game that reports no account id is untouched by this type,
/// and needs to be — a game with no id reports the name on both lines.
/// </para>
/// <para>
/// <b>Bounded, and never load-bearing.</b> Each instance keeps its most recently seen
/// <see cref="MaxNamesPerInstance"/> accounts and older rows fall off. Every operation is best-effort
/// in the same way the daemon's other stores are: a failed write is logged and swallowed, and a
/// corrupt file reads as empty. Losing the file costs a display name on a first join and nothing else
/// — the very next disconnect re-learns it.
/// </para>
/// <para>
/// Held in memory and written through on every change, so the join path (which reads it) does no file
/// IO. One process owns the file; a hot-swap re-exec drops the memory and the successor reads the
/// same file back, which is why the index survives a swap with no handoff entry of its own.
/// </para>
/// </summary>
internal sealed class PlayerNameStore(StatePathResolver paths, ILogger<PlayerNameStore> logger)
{
    /// <summary>
    /// How many accounts are remembered per instance. Comfortably above the population of a co-op
    /// server and of most community ones, so an actual regular is never forgotten, while an instance
    /// that has seen thousands of one-time visitors cannot grow the file without limit.
    /// </summary>
    public const int MaxNamesPerInstance = 200;

    private readonly Lock _gate = new();
    private PersistedPlayerNames? _names;

    /// <summary>
    /// The name this instance last reported for <paramref name="playerId"/>, or <see langword="null"/>
    /// when the account is unknown here — an honest absence the caller passes on as the missing field
    /// it already had, never as a placeholder.
    /// </summary>
    public string? Resolve(string instanceName, string? playerId)
    {
        if (string.IsNullOrEmpty(instanceName) || string.IsNullOrWhiteSpace(playerId))
            return null;

        lock (_gate)
        {
            try
            {
                PersistedPlayerNames names = Load();
                if (!names.Instances.TryGetValue(instanceName, out List<PlayerNameRecord>? rows))
                    return null;

                foreach (PlayerNameRecord row in rows)
                {
                    if (string.Equals(row.PlayerId, playerId, StringComparison.Ordinal))
                        return row.PlayerName;
                }

                return null;
            }
            catch (Exception ex)
            {
                // A lookup that cannot be answered is the state this index was added to improve on,
                // not a failure worth disturbing presence detection over.
                logger.LogWarning(ex, "could not read the player name index for {Instance}", instanceName);
                return null;
            }
        }
    }

    /// <summary>
    /// Record what this instance reported <paramref name="playerId"/> is called, moving the account to
    /// the front of the instance's rows. A blank id or a blank name is nothing to learn and is
    /// silently ignored — the pairing is the unit, and half of one identifies nobody.
    /// </summary>
    public void Learn(string instanceName, string? playerId, string? playerName)
    {
        if (string.IsNullOrEmpty(instanceName)
            || string.IsNullOrWhiteSpace(playerId)
            || string.IsNullOrWhiteSpace(playerName))
            return;

        lock (_gate)
        {
            try
            {
                PersistedPlayerNames names = Load();
                if (!names.Instances.TryGetValue(instanceName, out List<PlayerNameRecord>? rows))
                    names.Instances[instanceName] = rows = [];

                // The account moves to the front whether or not the name changed: the ordering is by
                // when it was last seen, which is what the cap evicts by, so a regular player must not
                // age out behind one-time visitors merely because their name never changes.
                rows.RemoveAll(r => string.Equals(r.PlayerId, playerId, StringComparison.Ordinal));
                rows.Insert(0, new PlayerNameRecord(playerId, playerName, DateTime.UtcNow));
                if (rows.Count > MaxNamesPerInstance)
                    rows.RemoveRange(MaxNamesPerInstance, rows.Count - MaxNamesPerInstance);

                Save(names);
                logger.LogDebug("{Instance}: account {PlayerId} is known as {PlayerName}",
                    instanceName, playerId, playerName);
            }
            catch (Exception ex)
            {
                // Failing to remember a name must not disturb the presence event that carried it.
                logger.LogWarning(ex, "could not record the player name for {Instance}", instanceName);
            }
        }
    }

    /// <summary>
    /// Drop an instance's rows entirely — used when the instance itself is removed, alongside the
    /// session map going with it. Deliberately NOT called when an instance merely stops: the index
    /// spans sessions, and forgetting it at the edge that ends one would leave it never able to
    /// answer the join it exists for.
    /// </summary>
    public void Forget(string instanceName)
    {
        if (string.IsNullOrEmpty(instanceName))
            return;

        lock (_gate)
        {
            try
            {
                PersistedPlayerNames names = Load();
                if (names.Instances.Remove(instanceName))
                    Save(names);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "could not clear the player name index for {Instance}", instanceName);
            }
        }
    }

    /// <summary>
    /// The whole index, read once and held. Returns empty on an absent or corrupt file (or any read
    /// error) — a bad file can never wedge presence detection; the next <see cref="Learn"/> rewrites
    /// it cleanly.
    /// </summary>
    private PersistedPlayerNames Load()
    {
        if (_names is not null)
            return _names;

        string path = ResolvePath();
        try
        {
            if (!File.Exists(path))
                return _names = new PersistedPlayerNames();

            PersistedPlayerNames? names = JsonSerializer.Deserialize(
                File.ReadAllText(path), WatchdogJsonContext.Default.PersistedPlayerNames);
            return _names = names ?? new PersistedPlayerNames();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not read the player name index {Path}; treating as empty", path);
            return _names = new PersistedPlayerNames();
        }
    }

    private void Save(PersistedPlayerNames names)
    {
        string path = ResolvePath();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(names, WatchdogJsonContext.Default.PersistedPlayerNames);

        // Atomic replace: write a sibling temp in the SAME directory (a cross-filesystem rename is
        // NOT atomic), then rename over the target so a reader never sees a half-written file.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private string ResolvePath() => paths.PathFor(StatePathResolver.PlayerNamesFile);
}
