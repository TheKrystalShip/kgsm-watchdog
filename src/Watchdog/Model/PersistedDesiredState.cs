namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// The on-disk shape of the daemon's desired-running set (written by <c>DesiredStateStore</c>). Holds
/// only operator <em>intent</em> — the names of instances that should be running — never spawn config:
/// that is re-read fresh from kgsm-lib on restore, so a config edit between boots is picked up and
/// there is no stale-spec drift. <see cref="Version"/> is carried for forward-compat (the project
/// versions its config schemas the same way), so a future field/shape change can migrate rather than
/// silently misread an older file.
/// </summary>
internal sealed class PersistedDesiredState
{
    /// <summary>Current on-disk schema version written by this build.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; }

    public List<string> DesiredRunning { get; set; } = [];
}
