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

    public static RunHistoryStore RunHistory(WatchdogOptions options) =>
        new(Resolver(options), NullLogger<RunHistoryStore>.Instance);
}
