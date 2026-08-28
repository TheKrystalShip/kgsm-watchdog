using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Lifecycle;
using TheKrystalShip.KGSM.Watchdog.Cgroup;
using TheKrystalShip.KGSM.Watchdog.Events;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// What this daemon reports about its own state.
/// </summary>
/// <remarks>
/// The boot path decides all of it: whether the daemon may spawn at all, whether per-instance
/// accounting will work, and whether a stop can take a whole process tree with it. Each was previously
/// a log line that nothing outside the process could act on.
/// </remarks>
public sealed class WatchdogLifecycleTests : IDisposable
{
    private readonly string _mount;

    public WatchdogLifecycleTests()
    {
        _mount = Path.Combine(Path.GetTempPath(), $"kgsm-wd-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_mount);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_mount, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone.
        }
    }

    [Fact]
    public void A_bootstrap_that_cannot_reach_a_delegated_cgroup_reports_the_reason_it_gave_health()
    {
        // The journal and /health must not be able to disagree about whether this daemon came up, so
        // both read SupervisorState. An empty temp mount is not a delegated cgroup, which is the
        // failure an operator meets when the unit is launched outside systemd.
        var recorder = new RecordingJournal();
        SupervisorState state = RunBootstrap(recorder);

        Assert.False(state.Ready);

        RecordingJournal.RecordedEvent degraded = Assert.Single(recorder.Recorded);
        Assert.Equal(LeafLifecycleEvents.Degraded, degraded.Type);
        Assert.Equal(WatchdogComponents.Delegation, degraded.String(LeafLifecycleFields.Component));
        Assert.Equal(state.Detail, degraded.String(LeafLifecycleFields.Detail));
    }

    [Fact]
    public void A_daemon_that_came_up_says_so_with_what_it_came_up_as()
    {
        var recorder = new RecordingJournal();
        var state = new SupervisorState { Ready = true, Detail = "delegated; base /x, in /x/supervisor" };

        Report(recorder, state);

        RecordingJournal.RecordedEvent ready = Assert.Single(recorder.Recorded);
        Assert.Equal(LeafLifecycleEvents.Ready, ready.Type);
        Assert.Equal(state.Detail, ready.String(LeafLifecycleFields.Detail));
    }

    [Fact]
    public void A_daemon_reporting_on_itself_is_the_author()
    {
        var recorder = new RecordingJournal();

        Report(recorder, new SupervisorState { Ready = true, Detail = "up" });

        RecordingJournal.RecordedEvent ready = Assert.Single(recorder.Recorded);
        Assert.Equal("system:watchdog", ready.Actor);
        Assert.Equal("system", ready.Origin);
    }

    [Fact]
    public void A_hot_swap_says_goodbye_as_a_reload_and_a_later_signal_does_not_overwrite_it()
    {
        // The distinction the whole reason field exists for. A swap replaces the image in place with
        // the same process id and not one supervised game restarted; reporting it as a stop would page
        // somebody on a successful deploy. MarkStopping writing once is what guarantees the reload
        // wins, since the swap always says it first.
        var recorder = new RecordingJournal();
        LeafLifecycle lifecycle = recorder.Lifecycle;

        Assert.True(lifecycle.MarkStopping(LeafStopReason.Reload));
        Assert.False(lifecycle.MarkStopping(LeafStopReason.Signal));

        RecordingJournal.RecordedEvent stopping = Assert.Single(recorder.Recorded);
        Assert.Equal(LeafLifecycleEvents.Stopping, stopping.Type);
        Assert.Equal(LeafStopReason.Reload, stopping.String(LeafLifecycleFields.Reason));
    }

    [Fact]
    public void An_ordinary_shutdown_says_goodbye_as_a_signal()
    {
        var recorder = new RecordingJournal();

        recorder.Lifecycle.MarkStopping(LeafStopReason.Signal);

        Assert.Equal(
            LeafStopReason.Signal,
            Assert.Single(recorder.Recorded).String(LeafLifecycleFields.Reason));
    }

    [Fact]
    public void The_components_this_daemon_can_report_are_distinct()
    {
        // Each id is a dedup key. Two components sharing one would report the second fault as the
        // first still being true, and recover from neither.
        string[] components =
            [WatchdogComponents.Delegation, WatchdogComponents.Controllers, WatchdogComponents.CgroupKill];

        Assert.Equal(components.Length, components.Distinct(StringComparer.Ordinal).Count());
        Assert.All(components, c => Assert.False(string.IsNullOrWhiteSpace(c)));
    }

    /// <summary>Runs the real bootstrap against a temp mount that is not a delegated cgroup.</summary>
    private SupervisorState RunBootstrap(RecordingJournal recorder)
    {
        var options = new WatchdogOptions { CgroupMountPoint = _mount };
        var cgroups = new CgroupManager(options, NullLogger<CgroupManager>.Instance);
        var state = new SupervisorState();

        new CgroupBootstrap(
            options, cgroups, state, recorder.Lifecycle, NullLogger<CgroupBootstrap>.Instance).Run();

        Report(recorder, state);
        return state;
    }

    /// <summary>
    /// The readiness report Program makes once the control socket is listening.
    /// </summary>
    /// <remarks>
    /// Mirrors the <c>ApplicationStarted</c> callback rather than invoking it, because reaching that
    /// callback means building a host that binds a socket. What is worth pinning is the decision — that
    /// the report reads <see cref="SupervisorState"/>, the same answer <c>/health</c> serves — and that
    /// is exactly this expression.
    /// </remarks>
    private static void Report(RecordingJournal recorder, SupervisorState state)
    {
        if (state.Ready)
            recorder.Lifecycle.MarkReady(state.Detail);
        else
            recorder.Lifecycle.MarkDegraded(WatchdogComponents.Delegation, state.Detail);
    }
}
