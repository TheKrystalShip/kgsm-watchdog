using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Client;

/// <summary>
/// The watchdog's control surface as a consumer sees it. Every answer is read from the body. A request that
/// cannot reach the daemon throws <see cref="WatchdogUnavailableException"/>, except where the answer is
/// documented as null or empty: an instance the daemon does not track, a console it does not have, a route
/// an older daemon does not serve.
/// </summary>
public interface IWatchdogClient : IDisposable
{
    /// <summary>The daemon's readiness. Never throws: an absent daemon is <see cref="WatchdogHealth.Reachable"/> false.</summary>
    Task<WatchdogHealth> HealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the daemon answers and is ready to spawn.</summary>
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>One supervised instance's state, or null when the daemon does not track it.</summary>
    Task<InstanceState?> StatusAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Every supervised instance.</summary>
    Task<IReadOnlyList<InstanceState>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The run clock of every instance the daemon can date, supervised or only remembered.</summary>
    Task<IReadOnlyList<InstanceRunTimes>> RunTimesAsync(CancellationToken cancellationToken = default);

    /// <summary>The names in the boot-autostart set.</summary>
    Task<IReadOnlyList<string>> EnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>The running daemon's build identity.</summary>
    Task<WatchdogVersionInfo> VersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts an instance. A capacity refusal is an answer: <see cref="ActionResult.Refusal"/> is <see cref="ActionRefusal.NoRoom"/>.</summary>
    Task<ActionResult> StartAsync(string instance, bool force = false, string? origin = null, CancellationToken cancellationToken = default);

    /// <summary>Stops an instance gracefully; the daemon drains for the instance's stop timeout before it kills.</summary>
    Task<ActionResult> StopAsync(string instance, string? origin = null, CancellationToken cancellationToken = default);

    /// <summary>Restarts an instance as one transition.</summary>
    Task<ActionResult> RestartAsync(string instance, string? origin = null, CancellationToken cancellationToken = default);

    /// <summary>Adds an instance to the boot-autostart set.</summary>
    Task<ActionResult> EnableAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Removes an instance from the boot-autostart set.</summary>
    Task<ActionResult> DisableAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Drops an instance from supervision, stopping it first. Not Ok while the instance is still live.</summary>
    Task<ActionResult> ForgetAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>Sets the CPU priority of a running instance's cgroup (<c>low</c>, <c>normal</c>, <c>high</c>).</summary>
    Task<ActionResult> SetCpuPriorityAsync(string instance, string priority, CancellationToken cancellationToken = default);

    /// <summary>Parks an instance for maintenance: deliberately down, crash-restart suppressed.</summary>
    Task<ActionResult> BeginMaintenanceAsync(string instance, string? origin = null, CancellationToken cancellationToken = default);

    /// <summary>Ends an instance's maintenance.</summary>
    Task<ActionResult> EndMaintenanceAsync(string instance, string? origin = null, CancellationToken cancellationToken = default);

    /// <summary>The runs of an instance's console, newest first; empty when it has no console.</summary>
    Task<IReadOnlyList<ConsoleRun>> ConsoleRunsAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>
    /// A window of one run's console. <paramref name="endOffset"/> below zero reads the tail; otherwise the
    /// window ends there, which is how a caller pages backwards. Empty when the instance has no console.
    /// </summary>
    Task<ConsoleWindow> ConsoleWindowAsync(string instance, int lines, int run = 0, long endOffset = -1, CancellationToken cancellationToken = default);

    /// <summary>The last lines of one run's console.</summary>
    Task<IReadOnlyList<string>> ConsoleTailAsync(string instance, int lines, int run = 0, CancellationToken cancellationToken = default);

    /// <summary>The console as it grows. The sequence ends only when the caller cancels; empty when the instance has no console.</summary>
    IAsyncEnumerable<string> FollowConsoleAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>One run's console as a stream, or null when the instance has no console. The caller disposes it.</summary>
    Task<ConsoleDownload?> OpenConsoleDownloadAsync(string instance, int run = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the daemon observes of every instance's players, keyed by instance name; null when the daemon
    /// cannot be asked or serves a shape this client does not read, which is not-knowing, never an empty host.
    /// </summary>
    Task<IReadOnlyDictionary<string, InstancePresence>?> PlayersAsync(CancellationToken cancellationToken = default);

    /// <summary>An instance's router port-forwards; null when the daemon cannot be asked or does not serve the route.</summary>
    Task<UpnpListResult?> UpnpAsync(string instance, CancellationToken cancellationToken = default);

    /// <summary>The memory admission gate's settings; null when the daemon does not serve them.</summary>
    Task<MemoryGateState?> MemoryGateAsync(CancellationToken cancellationToken = default);
}

/// <summary>A window of one console run and the byte range it covers.</summary>
/// <param name="Lines">The lines in the window.</param>
/// <param name="Start">The offset of the first byte of the window in the run.</param>
/// <param name="End">The offset after the last byte of the window; a page before this one ends at <see cref="Start"/>.</param>
public readonly record struct ConsoleWindow(IReadOnlyList<string> Lines, long Start, long End)
{
    public static ConsoleWindow Empty { get; } = new([], 0, 0);

    /// <summary>Whether the run holds output before this window.</summary>
    public bool HasEarlier => Start > 0;
}

/// <summary>One run's console as a stream with its length; disposing it releases the connection.</summary>
public sealed class ConsoleDownload(Stream content, long length, IDisposable owner) : IDisposable
{
    public Stream Content { get; } = content;

    public long Length { get; } = length;

    public void Dispose()
    {
        Content.Dispose();
        owner.Dispose();
    }
}
