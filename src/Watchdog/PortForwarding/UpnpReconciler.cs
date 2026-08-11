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
/// table pressure.
/// </para>
/// <para>
/// A forward two instances share is <b>not</b> one of those causes. They share one router row, and which
/// of them the row is tagged for says nothing about whether it is doing its job — so the diff compares
/// the row's target rather than its label, and a stop leaves a port its siblings still want mapped
/// (<see cref="IForwardedPortClaims"/>) instead of deleting it for this sweep to notice and repair.
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
        List<PortMapping> missing =
            MissingPorts(candidate.Spec.Ports, table.Mappings, candidate.Name, table.LocalAddress);
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

            // Releasing is still an ownerless delete, so it asks the same question a stop does: a port
            // another instance is running on is not this one's to take down, even when this one only
            // restored it a moment ago.
            await upnp.CloseAsync(
                candidate.Spec, missing, supervisor.ForwardedPortsHeldByOthers(candidate.Name),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        supervisor.NoteUpnpReasserted(candidate.Name, missing);
    }

    /// <summary>
    /// Which of an instance's configured ports the router is not currently forwarding for it. Pure, so
    /// the diff that decides whether to touch the router at all is unit-tested apart from the shell-out.
    /// <para>
    /// A row satisfies a configured port when it <em>is</em> the mapping this daemon would open for it:
    /// same external port and protocol, pointing at this host (<paramref name="localAddress"/>, as the
    /// IGD reported it) on the port it forwards. That is the honest test, because the description is a
    /// label and the forward is the fact. Two instances sharing an external port share one identical
    /// router row, and whichever opened it last owns the tag — matching on the tag alone would read that
    /// as the other one's forward having been dropped and re-open a mapping that is already correct,
    /// every sweep, for as long as both run.
    /// </para>
    /// <para>
    /// A row on the same external port pointing <em>somewhere else</em> — another host on the LAN, a
    /// different internal port, a hand-made rule — is not this mapping and still reads as missing. The
    /// tag is honoured as well, so a row this instance opened counts as its own even if the router
    /// rewrote the target.
    /// </para>
    /// <para>
    /// With no <paramref name="localAddress"/> the target cannot be checked and matching falls back to
    /// the tag alone: a listing that did not say where this host is cannot be used to conclude a row
    /// points at it.
    /// </para>
    /// <para>
    /// The result is re-collapsed into <see cref="PortMapping"/> ranges so what gets reported is the
    /// canonical shape the rest of the ecosystem carries, not the expanded single ports the comparison
    /// runs on.
    /// </para>
    /// </summary>
    internal static List<PortMapping> MissingPorts(
        IReadOnlyList<PortMapping> configured, IReadOnlyList<UpnpMapping> table, string instanceName,
        string? localAddress = null)
    {
        var held = new HashSet<(int Port, string Protocol)>();
        foreach (UpnpMapping m in table)
        {
            bool tagged = string.Equals(m.Description, instanceName, StringComparison.Ordinal);
            bool pointsHere = localAddress is { Length: > 0 }
                              && m.InternalPort == m.ExternalPort
                              && string.Equals(m.InternalClient, localAddress, StringComparison.OrdinalIgnoreCase);

            if (tagged || pointsHere)
                held.Add((m.ExternalPort, m.Protocol.ToLowerInvariant()));
        }

        return PortSets.Collapse(configured.Expand().Where(p => !held.Contains((p.Port, p.Protocol.ToLowerInvariant()))));
    }
}
