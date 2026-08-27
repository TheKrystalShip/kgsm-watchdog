using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The state stores, built against a test's own directory. Every store resolves its path through a
/// <see cref="StatePathResolver"/>, and a test that sets <c>WatchdogOptions.StateFile</c> pins the
/// whole set to that file's directory — so state written by one test cannot land in the real state
/// directory or be seen by another.
/// </summary>
internal static class TestState
{
    public static StatePathResolver Resolver(WatchdogOptions options) =>
        new(options, NullLogger<StatePathResolver>.Instance);

    public static DesiredStateStore Desired(WatchdogOptions options) =>
        new(Resolver(options), NullLogger<DesiredStateStore>.Instance);

    public static SupervisionStateStore Supervision(WatchdogOptions options) =>
        new(Resolver(options), NullLogger<SupervisionStateStore>.Instance);

    public static ReadinessStateStore Readiness(WatchdogOptions options) =>
        new(Resolver(options), NullLogger<ReadinessStateStore>.Instance);

    /// <summary>
    /// A readiness store for a test that does not exercise the cross-daemon latch. Pinned to a
    /// throwaway directory that is only created if something writes to it, so a test that never
    /// announces readiness for a nameable run leaves nothing behind.
    /// </summary>
    public static ReadinessStateStore Readiness() =>
        Readiness(new WatchdogOptions
        {
            StateFile = Path.Combine(
                Path.GetTempPath(), "kgsm-wd-ready-" + Guid.NewGuid().ToString("N"), "desired-state.json"),
        });

    public static RunHistoryStore RunHistory(WatchdogOptions options) =>
        new(Resolver(options), NullLogger<RunHistoryStore>.Instance);

    public static PlayerNameStore PlayerNames(WatchdogOptions options) =>
        new(Resolver(options), NullLogger<PlayerNameStore>.Instance);

    public static PlayerSessionStore Sessions(WatchdogOptions options) =>
        new(PlayerNames(options));

    /// <summary>
    /// A session store for a test that exercises session tracking and nothing about remembered names.
    /// Its name index is pinned to a throwaway directory that is only ever created if something writes
    /// to it, so a test that never learns a name leaves nothing behind.
    /// </summary>
    public static PlayerSessionStore Sessions() =>
        Sessions(new WatchdogOptions
        {
            StateFile = Path.Combine(
                Path.GetTempPath(), "kgsm-wd-names-" + Guid.NewGuid().ToString("N"), "desired-state.json"),
        });
}
