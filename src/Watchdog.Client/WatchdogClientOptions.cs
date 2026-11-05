namespace TheKrystalShip.KGSM.Watchdog.Client;

/// <summary>Where the watchdog's control socket is and how long a bounded request may take.</summary>
public sealed class WatchdogClientOptions
{
    /// <summary>The control socket.</summary>
    public string SocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>
    /// How long one bounded request may take. A stop drains for the instance's whole stop timeout, so the
    /// default outlasts the longest one. A console follow or download has no limit; the caller cancels.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(150);
}
