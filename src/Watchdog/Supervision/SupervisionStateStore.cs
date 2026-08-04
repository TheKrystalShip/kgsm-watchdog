using System.Text.Json;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The durable record of each instance's <b>supervision counters</b> — the restart streak, the give-up
/// latch, the phase, and the restart timing — persisted to <c>supervision-state.json</c> alongside the
/// <see cref="DesiredStateStore"/>'s <c>desired-state.json</c>. It exists so those counters survive
/// <b>any</b> daemon death (a planned hot-swap, but also an unclean SIGKILL/OOM), not only an orderly
/// restart: otherwise an OOM of the daemon would silently reset a crash-looping instance's failure
/// count to zero — fabricating health the operator never earned, the precise dishonesty the ecosystem
/// forbids.
/// <para>
/// This mirrors <see cref="DesiredStateStore"/> exactly — same lazily-resolved directory
/// (<c>Watchdog__StateFile</c>'s parent, else
/// <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm-watchdog</c>), same atomic same-dir temp+rename, same
/// best-effort posture (a failed write or a corrupt file degrades to "nothing to rehydrate", never
/// throws). Unlike the desired-state set, the on-disk shape here is a full snapshot
/// (<see cref="PersistedSupervisionState"/>), rewritten on each meaningful transition — cheap, since
/// counters only move on a crash/give-up/stability/stop.
/// </para>
/// <para>
/// All callers are post-bootstrap (the supervisor writes from inside the gate; rehydrate runs in a
/// hosted-service <c>StartAsync</c> after <c>CgroupBootstrap</c>), so the lazily-resolved default path
/// sees the dropped KGSM user's <c>HOME</c> and the access is already serialized — no internal locking.
/// </para>
/// </summary>
internal sealed class SupervisionStateStore(WatchdogOptions options, ILogger<SupervisionStateStore> logger)
{
    /// <summary>
    /// Read the persisted supervision counters. Returns an <b>empty</b> snapshot on an absent or corrupt
    /// file (or any read error) — a bad file can never wedge boot; the next <see cref="Save"/> rewrites
    /// it cleanly. Never throws.
    /// </summary>
    public PersistedSupervisionState Load()
    {
        string path = ResolvePath();
        try
        {
            if (!File.Exists(path))
                return new PersistedSupervisionState();

            var state = JsonSerializer.Deserialize(File.ReadAllText(path), WatchdogJsonContext.Default.PersistedSupervisionState);
            return state ?? new PersistedSupervisionState();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "could not read supervision-state file {Path}; treating as empty (counters start fresh this boot)", path);
            return new PersistedSupervisionState();
        }
    }

    /// <summary>
    /// Persist the full counter snapshot atomically (same-dir temp + rename, so a reader never sees a
    /// half-written file). Best-effort: a failed write is logged and swallowed — worst case the counters
    /// are stale after a restart, never a crashed supervisor.
    /// </summary>
    public void Save(PersistedSupervisionState state)
    {
        string path = ResolvePath();
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(state, WatchdogJsonContext.Default.PersistedSupervisionState);

            // Atomic replace: write a sibling temp in the SAME directory (a cross-filesystem rename is
            // NOT atomic), then rename over the target so a reader never sees a half-written file.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "could not persist supervision-state to {Path}; restart counters may be stale after a restart", path);
        }
    }

    /// <summary>
    /// The state-file path: <c>supervision-state.json</c> in the SAME directory the
    /// <see cref="DesiredStateStore"/> uses — the parent of an explicit <c>Watchdog__StateFile</c>
    /// if set, else <c>${XDG_DATA_HOME:-$HOME/.local/share}/kgsm-watchdog/</c>. Resolved lazily (never at
    /// options construction) because <c>HOME</c> only becomes the dropped KGSM user's after the bootstrap
    /// privilege drop, and every call site runs after that.
    /// </summary>
    private string ResolvePath()
    {
        if (!string.IsNullOrEmpty(options.StateFile))
        {
            // Companion file in the operator-chosen state dir (next to their desired-state.json).
            string dir = Path.GetDirectoryName(options.StateFile) ?? ".";
            return Path.Combine(dir, "supervision-state.json");
        }

        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/var/lib", ".local", "share");

        return Path.Combine(dataHome, "kgsm-watchdog", "supervision-state.json");
    }
}
