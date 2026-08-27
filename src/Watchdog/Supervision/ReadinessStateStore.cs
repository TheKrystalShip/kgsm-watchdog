using System.Text.Json;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The durable record of which run of each instance has already been announced ready, persisted to
/// <c>readiness-state.json</c> beside the <see cref="SupervisionStateStore"/>'s counters.
/// <para>
/// <c>instance-ready</c> is a transition and belongs to a run, but the daemon infers it from the first
/// time it sees an instance's cgroup populated — and for an instance that was already up, that is the
/// first tick after the daemon starts, every time the daemon starts. An in-process latch cannot tell
/// those apart because it dies with the process that would have to remember. Keyed to the run itself
/// (<see cref="ProcessStartClock.RunKeyFor"/>), this one can: a run any daemon already announced is
/// recognised on sight, and a genuinely new run carries a key that matches nothing.
/// </para>
/// <para>
/// It mirrors <see cref="SupervisionStateStore"/> exactly — same directory
/// (<see cref="StatePathResolver"/>), same atomic same-dir temp+rename, same best-effort posture where a
/// failed write or a corrupt file degrades to "nothing remembered" and never throws. The in-memory copy
/// is loaded on first use and is the one every read answers from, so the steady-state cost of the check
/// is a dictionary lookup.
/// </para>
/// <para>
/// The only caller is <see cref="NativePlayerPresenceIngester"/>'s single tick loop, so access is
/// already serialized and there is no internal locking.
/// </para>
/// </summary>
internal sealed class ReadinessStateStore(StatePathResolver paths, ILogger<ReadinessStateStore> logger)
{
    private PersistedReadinessState? _state;

    /// <summary>
    /// Whether <paramref name="runKey"/> is the run this instance's readiness was last announced for —
    /// so announcing it now would be repeating a transition rather than reporting one.
    /// </summary>
    public bool AlreadyAnnounced(string instanceName, string runKey) =>
        State.Instances.TryGetValue(instanceName, out InstanceReadinessState? entry)
        && string.Equals(entry.RunKey, runKey, StringComparison.Ordinal);

    /// <summary>
    /// Record that <paramref name="instanceName"/>'s readiness has been announced for
    /// <paramref name="runKey"/>, and persist it. One entry per instance, overwritten by each new run,
    /// so the file stays the size of the fleet.
    /// </summary>
    public void NoteAnnounced(string instanceName, string runKey, DateTime announcedAt)
    {
        State.Instances[instanceName] = new InstanceReadinessState
        {
            RunKey = runKey,
            AnnouncedAt = announcedAt,
        };
        Save();
    }

    private PersistedReadinessState State => _state ??= Load();

    /// <summary>
    /// Read the persisted announcements. An absent or corrupt file (or any read error) yields an empty
    /// snapshot — a bad file can never wedge boot, and the next <see cref="Save"/> rewrites it cleanly.
    /// The cost of starting empty is one repeated announcement per instance, never a missed one.
    /// </summary>
    private PersistedReadinessState Load()
    {
        string path = ResolvePath();
        try
        {
            if (!File.Exists(path))
                return new PersistedReadinessState();

            var state = JsonSerializer.Deserialize(File.ReadAllText(path), WatchdogJsonContext.Default.PersistedReadinessState);
            return state ?? new PersistedReadinessState();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "could not read readiness-state file {Path}; treating as empty (a live instance may be announced ready once more)", path);
            return new PersistedReadinessState();
        }
    }

    /// <summary>
    /// Persist the snapshot atomically (same-dir temp + rename, so a reader never sees a half-written
    /// file). Best-effort: a failed write is logged and swallowed — the worst it costs is a repeated
    /// announcement after the next daemon start, which is never worth failing an ingest pass over.
    /// </summary>
    private void Save()
    {
        string path = ResolvePath();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(_state, WatchdogJsonContext.Default.PersistedReadinessState);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "could not persist readiness-state to {Path}; a live instance may be announced ready again after a restart", path);
        }
    }

    /// <summary><c>readiness-state.json</c> in the resolved state directory — see <see cref="StatePathResolver"/>.</summary>
    private string ResolvePath() => paths.PathFor(StatePathResolver.ReadinessStateFile);
}
