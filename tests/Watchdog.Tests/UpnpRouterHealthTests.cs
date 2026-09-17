using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Lifecycle;
using TheKrystalShip.KGSM.Watchdog.Events;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The line between a router busy for a moment and a router whose UPnP service has gone silent — the
/// outage that left servers unreachable from the internet with nothing on the host saying so.
/// </summary>
public sealed class UpnpRouterHealthTests
{
    private readonly RecordingJournal _journal = new();
    private DateTimeOffset _now = new(2026, 9, 17, 4, 45, 0, TimeSpan.Zero);

    private UpnpRouterHealth Health(int reconcileSeconds = 300) =>
        new(_journal.Lifecycle, reconcileSeconds, NullLogger<UpnpRouterHealth>.Instance, () => _now);

    private void Advance(TimeSpan by) => _now += by;

    private List<RecordingJournal.RecordedEvent> Transitions() =>
        [.. _journal.Recorded.Where(e => e.Type is LeafLifecycleEvents.Degraded or LeafLifecycleEvents.Recovered)];

    [Fact]
    public void One_unanswered_call_is_not_an_outage()
    {
        UpnpRouterHealth health = Health();

        health.Unanswered("discovery got no answer");

        Assert.Empty(Transitions());
        Assert.False(health.IsDegraded);
    }

    [Fact]
    public void Calls_unanswered_across_the_span_report_the_router_degraded_once()
    {
        UpnpRouterHealth health = Health();

        // A sweep every five minutes: the fourth unanswered sweep is the one that has spanned fifteen.
        for (int sweep = 0; sweep < 6; sweep++)
        {
            health.Unanswered("discovery got no answer");
            Advance(TimeSpan.FromMinutes(5));
        }

        RecordingJournal.RecordedEvent degraded = Assert.Single(Transitions());
        Assert.Equal(LeafLifecycleEvents.Degraded, degraded.Type);
        Assert.Equal(WatchdogComponents.UpnpRouter, degraded.String(LeafLifecycleFields.Component));
        Assert.Contains("15 minutes", degraded.String(LeafLifecycleFields.Detail));
        Assert.Contains("discovery got no answer", degraded.String(LeafLifecycleFields.Detail));
        Assert.True(health.IsDegraded);
    }

    [Fact]
    public void An_answer_inside_the_span_starts_the_count_again()
    {
        UpnpRouterHealth health = Health();

        health.Unanswered("discovery got no answer");
        Advance(TimeSpan.FromMinutes(10));
        health.Answered();
        Advance(TimeSpan.FromMinutes(5));
        health.Unanswered("discovery got no answer");
        Advance(TimeSpan.FromMinutes(10));
        health.Unanswered("discovery got no answer");

        Assert.Empty(Transitions());
    }

    [Fact]
    public void Two_failures_hours_apart_say_nothing_about_the_time_between_them()
    {
        // Nothing forwarding and nothing reported means nothing asks the router between two starts, and
        // two unanswered starts three hours apart cannot be counted as three hours of silence.
        UpnpRouterHealth health = Health();

        health.Unanswered("discovery got no answer");
        Advance(TimeSpan.FromHours(3));
        health.Unanswered("discovery got no answer");

        Assert.Empty(Transitions());
    }

    [Fact]
    public void The_first_answer_after_an_outage_reports_the_recovery()
    {
        UpnpRouterHealth health = Health();

        for (int sweep = 0; sweep < 4; sweep++)
        {
            health.Unanswered("discovery got no answer");
            Advance(TimeSpan.FromMinutes(5));
        }

        health.Answered();
        health.Answered();

        Assert.Equal(
            [LeafLifecycleEvents.Degraded, LeafLifecycleEvents.Recovered],
            Transitions().Select(e => e.Type));
        Assert.False(health.IsDegraded);
    }

    [Fact]
    public void An_answer_with_nothing_reported_writes_nothing()
    {
        Health().Answered();

        Assert.Empty(Transitions());
    }

    [Fact]
    public void A_sweep_slower_than_the_span_still_reports_after_two_unanswered_sweeps()
    {
        // With a half-hour sweep the observations are always wider apart than fifteen minutes, so a gap
        // that size must not reset the count or a slow sweep could never report anything.
        UpnpRouterHealth health = Health(reconcileSeconds: 1800);

        health.Unanswered("discovery got no answer");
        Advance(TimeSpan.FromMinutes(30));
        Assert.Empty(Transitions());

        health.Unanswered("discovery got no answer");

        Assert.Equal(LeafLifecycleEvents.Degraded, Assert.Single(Transitions()).Type);
    }
}
