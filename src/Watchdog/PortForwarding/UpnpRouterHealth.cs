using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Lifecycle;
using TheKrystalShip.KGSM.Watchdog.Events;

namespace TheKrystalShip.KGSM.Watchdog.PortForwarding;

/// <summary>
/// Whether the router's UPnP service is answering, reported as a degraded part of this daemon when it
/// has stopped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Measured on hotrod: after a WAN reconnect the router's UPnP service stopped
/// answering discovery, and stayed silent until its UPnP setting was switched off and on, while the router
/// itself routed traffic normally. Every open failed, every sweep found the router unreachable and
/// correctly did nothing, and nothing said so — the servers it was meant to forward sat unreachable from
/// the internet until somebody looked at the router's table. The sweep holding off is right; the silence
/// is not.
/// </para>
/// <para>
/// <b>Every <c>upnpc</c> call is an observation.</b> A start's open, a stop's close, a sweep's listing
/// and a control-plane query all learn whether a router answered, so the tracker is fed from the one
/// place they all go through rather than from a probe of its own.
/// </para>
/// <para>
/// <b>An episode, not a failure.</b> One unanswered call is a router busy for a moment. The component is
/// reported degraded only when calls have gone unanswered across at least <see cref="DegradedAfter"/>
/// with no gap between observations wide enough to hide an answer — two failures hours apart say nothing
/// about the time between them. The first answer ends the episode and reports the recovery.
/// </para>
/// <para>
/// Whether the component is degraded is <see cref="LeafLifecycle"/>'s answer and only its, so the report,
/// its recovery and <see cref="IsDegraded"/> cannot disagree. This daemon is resident and starts from a
/// clean slate, so an outage that outlives a restart is measured afresh and reported again once it has
/// lasted <see cref="DegradedAfter"/> in the new process.
/// </para>
/// </remarks>
internal sealed class UpnpRouterHealth
{
    /// <summary>
    /// How long calls must go unanswered before the router is reported. Long enough that a router reboot
    /// passes unremarked, short enough that the report arrives within the hour an outage costs players.
    /// </summary>
    internal static readonly TimeSpan DegradedAfter = TimeSpan.FromMinutes(15);

    private readonly LeafLifecycle _lifecycle;
    private readonly ILogger<UpnpRouterHealth> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _span;
    private readonly TimeSpan _maxGap;
    private readonly Lock _gate = new();

    private DateTimeOffset? _unansweredSince;
    private DateTimeOffset _lastUnanswered;

    public UpnpRouterHealth(LeafLifecycle lifecycle, WatchdogOptions options, ILogger<UpnpRouterHealth> logger)
        : this(lifecycle, options.UpnpReconcileSeconds, logger, static () => DateTimeOffset.UtcNow) { }

    /// <summary>
    /// The clock is a function so an episode can be exercised against scripted time. What this class
    /// decides — a moment's silence against an outage — cannot be proved by waiting a quarter of an hour.
    /// </summary>
    /// <param name="lifecycle">Where the degradation is reported.</param>
    /// <param name="reconcileSeconds">
    /// The sweep interval, which sets how far apart observations normally are. A sweep slower than
    /// <see cref="DegradedAfter"/> stretches both the span and the tolerated gap to cover two sweeps, or a
    /// slow sweep could never observe an episode long enough to report.
    /// </param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The clock.</param>
    internal UpnpRouterHealth(
        LeafLifecycle lifecycle, int reconcileSeconds, ILogger<UpnpRouterHealth> logger, Func<DateTimeOffset> clock)
    {
        _lifecycle = lifecycle;
        _logger = logger;
        _clock = clock;

        TimeSpan sweep = TimeSpan.FromSeconds(Math.Max(0, reconcileSeconds));
        _span = sweep > DegradedAfter ? sweep : DegradedAfter;
        _maxGap = sweep * 2 > DegradedAfter ? sweep * 2 : DegradedAfter;
    }

    /// <summary>Whether the router is currently reported unreachable.</summary>
    /// <remarks>
    /// The sweep reads this to keep asking the router while nothing running needs it: a recovery that is
    /// never observed is never reported.
    /// </remarks>
    public bool IsDegraded => _lifecycle.DegradedComponents.Contains(WatchdogComponents.UpnpRouter);

    /// <summary>A router answered.</summary>
    public void Answered()
    {
        lock (_gate)
        {
            _unansweredSince = null;
        }

        if (_lifecycle.MarkRecovered(WatchdogComponents.UpnpRouter))
            _logger.LogInformation("UPnP: the router is answering again");
    }

    /// <summary>A call reached no router.</summary>
    /// <param name="reason">Why, in words that belong in the report: no answer, a timeout, no client.</param>
    public void Unanswered(string reason)
    {
        DateTimeOffset now = _clock();
        TimeSpan unansweredFor;

        lock (_gate)
        {
            // A gap wider than observations are ever apart means nothing was watching in between, so the
            // silence before it cannot be counted toward this one.
            if (_unansweredSince is null || now - _lastUnanswered > _maxGap)
                _unansweredSince = now;

            _lastUnanswered = now;
            unansweredFor = now - _unansweredSince.Value;
        }

        if (unansweredFor < _span)
            return;

        int minutes = (int)Math.Round(unansweredFor.TotalMinutes);
        string detail =
            $"No router has answered UPnP for {minutes} minutes ({reason}). Running servers' port forwards " +
            "cannot be opened or restored, so they are unreachable from the internet until it does.";

        if (_lifecycle.MarkDegraded(WatchdogComponents.UpnpRouter, detail))
            _logger.LogWarning("UPnP: {Detail}", detail);
    }
}
