using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.PortForwarding;

/// <summary>
/// Puts back router forwards the IGD dropped underneath a running instance.
/// <para>
/// A UPnP mapping is not durable the way a host firewall rule is. A router can accept a mapping,
/// report its lease as infinite, and discard it anyway — on a WAN reconnect, a reboot, table pressure,
/// or nothing visible at all — and when it does, a game server keeps running while quietly becoming
/// unreachable from outside. Nothing on this host is told.
/// </para>
/// <para>
/// So this reconciles rather than renews. There is no expiry to schedule against: the lease the router
/// advertises is exactly the number that turns out not to bind it, and <c>upnpc -r</c> takes no duration
/// to negotiate one. What holds instead is the same rule the supervisor already applies to cgroups —
/// measure what is actually there, compare it to what should be, and close the difference. That covers
/// every cause uniformly, including the ones with no signal attached: a silent drop, a router reboot,
/// and a sibling instance whose stop deleted a shared external port out from under this one
/// (<c>upnpc -f</c> deletes by port, with no owner check).
/// </para>
/// <para>
/// The sweep is deliberately cheap and deliberately timid. It costs nothing while no forwarding
/// instance runs, reads the whole IGD table in one invocation however many instances it covers, and
/// touches the router only to add back a mapping it has confirmed is missing. A router it cannot reach
/// leaves it doing nothing at all — an unreadable table is not evidence of an empty one, and treating
/// it as such would turn a brief router outage into a storm of redundant re-opens.
/// </para>
/// </summary>
internal sealed class UpnpReconciler(
    InstanceSupervisor supervisor,
    UpnpService upnp,
    WatchdogOptions options,
    ILogger<UpnpReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.UpnpReconcileSeconds <= 0)
        {
            logger.LogInformation("UPnP reconcile disabled (upnpReconcileSeconds=0)");
            return;
        }

        var interval = TimeSpan.FromSeconds(options.UpnpReconcileSeconds);
        logger.LogInformation("UPnP reconcile started; sweeping every {Seconds}s", interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Same posture as the supervision loop: a sweep that throws is logged and the loop
                    // lives on. Port forwarding is an opt-in convenience and must never be able to take
                    // the daemon down with it.
                    logger.LogError(ex, "UPnP reconcile sweep threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        logger.LogInformation("UPnP reconcile stopped");
    }

    /// <summary>One pass: read the router once, then restore whatever a running instance is missing.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        ForwardingCandidate[] candidates = supervisor.ForwardingCandidates();
        if (candidates.Length == 0)
            return; // nothing forwarding → never touch the router at all

        UpnpTable table = await upnp.ListAllAsync(ct).ConfigureAwait(false);
        if (!table.Reached)
        {
            // We do not know what the router holds. Doing nothing is the only honest move: an
            // unreachable IGD read as an empty table would re-open every forward on every sweep.
            logger.LogDebug("UPnP reconcile: router unreachable, skipping sweep");
            return;
        }

        foreach (ForwardingCandidate candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            await ReassertAsync(candidate, table, ct).ConfigureAwait(false);
        }
    }

    private async Task ReassertAsync(ForwardingCandidate candidate, UpnpTable table, CancellationToken ct)
    {
        List<PortMapping> missing = MissingPorts(candidate.Spec.Ports, table.Mappings, candidate.Name);
        if (missing.Count == 0)
            return;

        logger.LogWarning(
            "UPnP reconcile: router dropped {Count} forward(s) for {Instance} ({Ports}) while it was running; re-asserting",
            missing.Count, candidate.Name, missing.ToUfwSpec());

        UpnpOutcome outcome = await upnp.OpenAsync(candidate.Spec, missing, ct).ConfigureAwait(false);
        if (outcome != UpnpOutcome.Applied)
        {
            // Skipped (gated off since the candidate was picked) or Failed (upnpc could not deliver).
            // UpnpService has already logged the detail; emitting here would claim a mapping we do not
            // have. The next sweep tries again.
            return;
        }

        // The instance can have stopped while upnpc was mid-call, in which case the stop's own close ran
        // before this open and we have just re-created a forward for something that is no longer running
        // — the exact state the open-on-start/close-on-stop lifetime exists to prevent. Undo it, and
        // report nothing: from the audit trail's point of view this re-assert never happened.
        if (!supervisor.IsRunning(candidate.Name))
        {
            logger.LogInformation(
                "UPnP reconcile: {Instance} stopped mid-re-assert; releasing the forwards just restored",
                candidate.Name);
            await upnp.CloseAsync(candidate.Spec, missing, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        supervisor.NoteUpnpReasserted(candidate.Name, missing);
    }

    /// <summary>
    /// Which of an instance's configured ports the router is not currently forwarding for it. Pure, so
    /// the diff that decides whether to touch the router at all is unit-tested apart from the shell-out.
    /// <para>
    /// A row counts as this instance's only when its description matches the ownership tag the watchdog
    /// writes on open (<c>upnpc -e &lt;name&gt;</c>). A mapping on the same external port owned by
    /// something else — another host on the LAN, a different instance, a hand-made rule — is deliberately
    /// NOT counted as ours: re-opening would silently steal the port from its owner, and this daemon only
    /// ever asserts mappings it can claim.
    /// </para>
    /// <para>
    /// The result is re-collapsed into <see cref="PortMapping"/> ranges so what gets reported is the
    /// canonical shape the rest of the ecosystem carries, not the expanded single ports the comparison
    /// runs on.
    /// </para>
    /// </summary>
    internal static List<PortMapping> MissingPorts(
        IReadOnlyList<PortMapping> configured, IReadOnlyList<UpnpMapping> table, string instanceName)
    {
        var held = new HashSet<(int Port, string Protocol)>(
            table
                .Where(m => string.Equals(m.Description, instanceName, StringComparison.Ordinal))
                .Select(m => (m.ExternalPort, m.Protocol.ToLowerInvariant())));

        List<(int Port, string Protocol)> gaps =
            [.. configured.Expand().Where(p => !held.Contains((p.Port, p.Protocol.ToLowerInvariant())))];

        return Collapse(gaps);
    }

    /// <summary>
    /// Re-collapse expanded (port, protocol) pairs into contiguous <see cref="PortMapping"/> ranges,
    /// grouped by protocol — the inverse of <c>Expand()</c>, so a gap spanning a whole configured range
    /// is reported as that range rather than as N single ports.
    /// </summary>
    private static List<PortMapping> Collapse(List<(int Port, string Protocol)> ports)
    {
        var result = new List<PortMapping>();
        foreach (var group in ports.GroupBy(p => p.Protocol, StringComparer.OrdinalIgnoreCase))
        {
            int[] sorted = [.. group.Select(p => p.Port).Distinct().Order()];
            for (int i = 0; i < sorted.Length;)
            {
                int start = sorted[i];
                int end = start;
                while (i + 1 < sorted.Length && sorted[i + 1] == end + 1)
                {
                    end = sorted[++i];
                }

                i++;
                result.Add(new PortMapping { Start = start, End = end, Protocol = group.Key });
            }
        }

        return result;
    }
}
