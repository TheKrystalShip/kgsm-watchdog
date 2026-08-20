using System.Text.Json;

using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The durable ledger of how each run ended, persisted to <c>run-history.json</c> in the state
/// directory (<see cref="StatePathResolver"/>).
/// <para>
/// <b>Why it is here and not beside the log.</b> The natural place for "this run crashed" is a file
/// next to the run's console — but an instance's directory belongs to the instance, and the daemon
/// writing bookkeeping into it makes the game's own layout something two owners edit. The ledger
/// instead lives with the daemon's other state, and joins to a console file on
/// <see cref="RunRecord.EndedAt"/>, which is that file's own mtime and survives rotation.
/// </para>
/// <para>
/// <b>Bounded, and never load-bearing.</b> Each instance keeps its most recent
/// <see cref="MaxRunsPerInstance"/> runs and older rows fall off, so an instance in a crash loop
/// cannot grow the file without limit. Every operation is best-effort in the same way the other two
/// stores are: a failed write is logged and swallowed, and a corrupt file reads as empty. A missing
/// row is an honest unknown — a caller that cannot find a run's outcome must say so, never infer
/// that the run ended cleanly.
/// </para>
/// <para>
/// Writes happen under the supervisor's gate, from the same transitions that persist the counters.
/// Reads come from the control surface off the gate, so each one re-reads the file rather than
/// sharing a cached snapshot: the file is small, the endpoint is not hot, and a reader that never
/// caches cannot serve a stale outcome.
/// </para>
/// </summary>
internal sealed class RunHistoryStore(StatePathResolver paths, ILogger<RunHistoryStore> logger)
{
    /// <summary>
    /// How many runs are kept per instance. Deep enough to cover a crash loop and the runs on either
    /// side of it — which is the whole span anything correlating a crash against console output looks
    /// at — without letting a looping instance grow the file unboundedly.
    /// </summary>
    public const int MaxRunsPerInstance = 20;

    /// <summary>
    /// Record how a run ended. Newest-first within the instance, capped at
    /// <see cref="MaxRunsPerInstance"/>. Never throws.
    /// </summary>
    public void Record(string instance, RunRecord run)
    {
        try
        {
            var history = Load();
            if (!history.Instances.TryGetValue(instance, out var runs))
                history.Instances[instance] = runs = [];

            // A run is recorded once, by the transition that first concluded it. A crash that leaves a
            // restart pending, and an operator who then cancels that restart, are two transitions over
            // ONE run: the second would otherwise write a second row for the same console file and
            // re-label a crash as a stop. Whichever transition saw the run end first is the one that
            // classified it correctly, so the existing row stands.
            if (runs.Count > 0 && runs[0].EndedAt == run.EndedAt)
            {
                logger.LogDebug("{Instance}'s run ending {EndedAt:o} is already recorded as {Outcome}; "
                    + "leaving it", instance, run.EndedAt, runs[0].Outcome);
                return;
            }

            runs.Insert(0, run);
            if (runs.Count > MaxRunsPerInstance)
                runs.RemoveRange(MaxRunsPerInstance, runs.Count - MaxRunsPerInstance);

            Save(history);
            logger.LogDebug("recorded {Instance} run ending {EndedAt:o} as {Outcome}",
                instance, run.EndedAt, run.Outcome);
        }
        catch (Exception ex)
        {
            // The ledger is an aid to correlating a crash with its output, not part of supervising
            // one. Failing to write it must not disturb the transition that produced it.
            logger.LogWarning(ex, "could not record the run ledger entry for {Instance}", instance);
        }
    }

    /// <summary>
    /// One instance's runs, newest first. Empty for an instance with no recorded runs — which is the
    /// same answer as a ledger that could not be read, and is why a caller reports a missing outcome
    /// as unknown rather than as anything about how the run ended.
    /// </summary>
    public IReadOnlyList<RunRecord> RunsFor(string instance) =>
        Load().Instances.TryGetValue(instance, out var runs) ? runs : [];

    /// <summary>
    /// When each instance's most recent run ended, from ONE read of the ledger. The list endpoint
    /// reports this for every tracked instance at once, and asking <see cref="RunsFor"/> per instance
    /// would re-read the file once per row.
    /// </summary>
    public IReadOnlyDictionary<string, DateTime> LastEndedByInstance()
    {
        var latest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var (instance, runs) in Load().Instances)
            if (runs.Count > 0)
                latest[instance] = runs[0].EndedAt;   // rows are held newest-first
        return latest;
    }

    /// <summary>When one instance's most recent run ended, or null when it has no recorded runs.</summary>
    public DateTime? LastEndedFor(string instance)
    {
        IReadOnlyList<RunRecord> runs = RunsFor(instance);
        return runs.Count > 0 ? runs[0].EndedAt : null;
    }

    /// <summary>Drop an instance's rows entirely — used when the instance itself is removed.</summary>
    public void Forget(string instance)
    {
        try
        {
            var history = Load();
            if (history.Instances.Remove(instance))
                Save(history);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not clear the run ledger for {Instance}", instance);
        }
    }

    /// <summary>
    /// The whole ledger. Returns empty on an absent or corrupt file (or any read error) — a bad file
    /// can never wedge a transition; the next <see cref="Record"/> rewrites it cleanly.
    /// </summary>
    private PersistedRunHistory Load()
    {
        string path = ResolvePath();
        try
        {
            if (!File.Exists(path))
                return new PersistedRunHistory();

            var history = JsonSerializer.Deserialize(
                File.ReadAllText(path), WatchdogJsonContext.Default.PersistedRunHistory);
            return history ?? new PersistedRunHistory();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "could not read the run ledger {Path}; treating as empty", path);
            return new PersistedRunHistory();
        }
    }

    private void Save(PersistedRunHistory history)
    {
        string path = ResolvePath();
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string json = JsonSerializer.Serialize(history, WatchdogJsonContext.Default.PersistedRunHistory);

        // Atomic replace: write a sibling temp in the SAME directory (a cross-filesystem rename is
        // NOT atomic), then rename over the target so a reader never sees a half-written file.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    private string ResolvePath() => paths.PathFor(StatePathResolver.RunHistoryFile);
}
