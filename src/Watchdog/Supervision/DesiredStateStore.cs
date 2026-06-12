using System.Text.Json;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The durable record of which instances are desired-running, so the daemon can restore supervision
/// after a restart or host reboot — the in-house replacement for systemd's <c>systemctl enable</c> +
/// <c>WantedBy=</c>. It persists <b>operator intent only</b> (the set of instance names); the spawn
/// config is re-read from kgsm-lib on restore, so there is no stale-spec drift.
/// <para>
/// Mutation is <b>incremental</b> (<see cref="Add"/>/<see cref="Remove"/>), not a snapshot of the live
/// supervisor table. That is deliberate: an instance whose config kgsm-lib can't read at restore time
/// (a transient miss) must NOT be silently dropped from auto-start — intent persists until an
/// <em>explicit</em> stop removes it, exactly like a systemd unit stays enabled until <c>disable</c>.
/// </para>
/// <para>
/// All callers are post-bootstrap (restore runs in a hosted-service <c>StartAsync</c> after
/// <c>CgroupBootstrap</c>; <see cref="Add"/>/<see cref="Remove"/> run inside control verbs under the
/// supervisor gate), so the lazily-resolved default path sees the dropped user's <c>HOME</c>, and the
/// load/modify/save sequence is already serialized — no internal locking needed.
/// </para>
/// </summary>
internal sealed class DesiredStateStore(WatchdogOptions options, ILogger<DesiredStateStore> logger)
{
    /// <summary>The set of instance names currently marked desired-running (deduped, order-insensitive).</summary>
    public IReadOnlyList<string> Load()
    {
        string path = ResolvePath();
        try
        {
            if (!File.Exists(path))
                return [];

            var state = JsonSerializer.Deserialize(File.ReadAllText(path), WatchdogJsonContext.Default.PersistedDesiredState);
            return state?.DesiredRunning?
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            // A corrupt/half-written file must never wedge boot — treat it as "nothing to restore"
            // (the next Add/Remove rewrites it cleanly) rather than throwing and aborting startup.
            logger.LogWarning(ex, "could not read desired-state file {Path}; treating as empty (no auto-start this boot)", path);
            return [];
        }
    }

    /// <summary>Mark <paramref name="name"/> desired-running (idempotent). Called when an instance is started.</summary>
    public void Add(string name)
    {
        var set = new SortedSet<string>(Load(), StringComparer.Ordinal);
        if (set.Add(name))
            Save(set);
    }

    /// <summary>Drop <paramref name="name"/> from the desired-running set (idempotent). Called on a deliberate stop.</summary>
    public void Remove(string name)
    {
        var set = new SortedSet<string>(Load(), StringComparer.Ordinal);
        if (set.Remove(name))
            Save(set);
    }

    private void Save(IReadOnlyCollection<string> names)
    {
        string path = ResolvePath();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var state = new PersistedDesiredState
            {
                Version = PersistedDesiredState.CurrentVersion,
                DesiredRunning = names.ToList(),
            };
            string json = JsonSerializer.Serialize(state, WatchdogJsonContext.Default.PersistedDesiredState);

            // Atomic replace: write a sibling temp in the SAME directory (a cross-filesystem rename is
            // NOT atomic), then rename over the target so a reader never sees a half-written file.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Persistence is best-effort: a failed write must not break a start/stop the operator asked
            // for. Worst case the auto-start set is stale after a restart — logged, not fatal.
            logger.LogError(ex, "could not persist desired-state to {Path}; auto-start may be stale after a restart", path);
        }
    }

    /// <summary>
    /// The state-file path: the explicit <c>KGSM_WATCHDOG_STATE_FILE</c> if set, else
    /// <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm-watchdog/desired-state.json</c>. Resolved lazily
    /// (never at options construction) because <c>HOME</c> only becomes the dropped KGSM user's after
    /// the bootstrap privilege drop, and every call site runs after that.
    /// </summary>
    private string ResolvePath()
    {
        if (!string.IsNullOrEmpty(options.StateFile))
            return options.StateFile;

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/var/lib", ".local", "share");

        return Path.Combine(dataHome, "kgsm-watchdog", "desired-state.json");
    }
}
