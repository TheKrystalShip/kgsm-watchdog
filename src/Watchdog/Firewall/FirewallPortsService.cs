using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Exceptions;

namespace TheKrystalShip.KGSM.Watchdog.Firewall;

/// <summary>
/// The outcome of a host-firewall open/close attempt, so the supervisor emits an audit event ONLY on a
/// confirmed transition (never a fabricated one). Deliberately the same three-token shape as
/// <see cref="PortForwarding.UpnpOutcome"/> — the two bring-up side effects report identically.
/// <list type="bullet">
/// <item><see cref="Skipped"/> — gated off (firewall management disabled or no ports), or the authority
/// had nothing to change — nothing happened, no event.</item>
/// <item><see cref="Applied"/> — the authority confirmed the rule change — emit the event.</item>
/// <item><see cref="Failed"/> — the operator asked for it and the authority could not deliver
/// (unreachable, unsupported backend, or a rejected rule) — logged, no event.</item>
/// </list>
/// </summary>
internal enum FirewallPortsOutcome { Skipped, Applied, Failed }

/// <summary>
/// Opens / closes a native instance's host-firewall rule through the kgsm-firewall authority, so an
/// instance's ports are open exactly while it is running. The watchdog owns the trigger for the same
/// reason it owns UPnP: this is <b>process-lifetime</b> state — the rule must open on a fresh bring-up,
/// survive a crash-restart (a dead process does not drop a host rule, and the restart needs it), and
/// close on an intended stop. Only the supervisor observes those transitions precisely, and it observes
/// them for boot-autostart and crash-respawn too, which nothing in the kgsm CLI ever sees.
/// <para>
/// It owns the <em>trigger</em>, not the firewall: every write goes through kgsm-lib's
/// <see cref="IFirewallService"/> to the kgsm-firewall authority, which stays the single owner of host
/// firewall state. This type adds only the per-instance gate, the outcome mapping, and honest logging.
/// </para>
/// <para>
/// <b>Best-effort, and louder than UPnP.</b> Like UPnP, a failure never fails a start — a firewall
/// authority that is down must not keep a game server off the air. Unlike UPnP, a failed <em>open</em>
/// leaves the server unreachable to everyone rather than only to off-LAN players, so a failure is logged
/// at warning with the instance named. The Control Panel learns the ports were never confirmed open by
/// there being no <c>instance_ports_opened</c> line in kgsm-firewall's journal — an authority that
/// changed nothing records nothing.
/// </para>
/// <para>
/// AOT-safe: the client serializes through the Firewall.Contracts source-generated context, so there is
/// no reflection on this path.
/// </para>
/// </summary>
internal sealed class FirewallPortsService(IFirewallService firewall, ILogger<FirewallPortsService> logger)
{
    /// <summary>
    /// Open the instance's host-firewall rule (no-op unless <c>enable_firewall_management</c>).
    /// </summary>
    /// <remarks>
    /// The outcome is returned for logging and for the caller to reason about, NOT so it can record the
    /// edge: kgsm-firewall performs the change and writes its own <c>instance_ports_opened</c> line.
    /// <paramref name="actor"/> and <paramref name="origin"/> ride along so that record can say who
    /// asked — the authority repeats the claim and cannot check it, which is why it must come from here.
    /// </remarks>
    public async Task<FirewallPortsOutcome> OpenAsync(
        Instance instance, string? actor = null, string? origin = null, CancellationToken ct = default)
    {
        // Per-instance gate, the mirror of UPnP's enable_port_forwarding check. Disabled is the operator
        // saying "do not manage this instance's firewall", which a bring-up must not override.
        if (!instance.EnableFirewallManagement)
            return FirewallPortsOutcome.Skipped;

        // The authority is declarative: it sets exactly the ports handed to it. An instance that declares
        // none has nothing to open, and asking would be a request to own an empty rule set.
        if (instance.Ports.Count == 0)
        {
            logger.LogInformation(
                "firewall open skipped for {Instance}: no ports configured", instance.Name);
            return FirewallPortsOutcome.Skipped;
        }

        return await InvokeAsync(
            "open", instance.Name,
            () => firewall.EnsureOpenAsync(instance.Name, instance.Ports, actor, origin, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove the instance's host-firewall rule on a deliberate stop (no-op unless
    /// <c>enable_firewall_management</c>). No port check: removal is addressed by the instance's
    /// ownership tag, so it cleans up correctly even for an instance whose declared ports have changed.
    /// </summary>
    public async Task<FirewallPortsOutcome> CloseAsync(
        Instance instance, string? actor = null, string? origin = null, CancellationToken ct = default)
    {
        if (!instance.EnableFirewallManagement)
            return FirewallPortsOutcome.Skipped;

        return await InvokeAsync(
            "close", instance.Name,
            () => firewall.RemoveAsync(instance.Name, actor, origin, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one authority call and maps its reply onto <see cref="FirewallPortsOutcome"/>. An unreachable
    /// authority throws out of the client rather than returning a result, so it is caught here and
    /// reported as <see cref="FirewallPortsOutcome.Failed"/> — the caller never sees an exception, and never
    /// mistakes "could not ask" for "nothing needed doing".
    /// </summary>
    private async Task<FirewallPortsOutcome> InvokeAsync(
        string action, string instanceName, Func<Task<FirewallActionResult>> call)
    {
        try
        {
            FirewallActionResult result = await call().ConfigureAwait(false);

            switch (result.Outcome)
            {
                // A real transition — the only outcome worth an audit event.
                case Core.Models.FirewallOutcome.Applied:
                case Core.Models.FirewallOutcome.Removed:
                    return FirewallPortsOutcome.Applied;

                // The rule is written but the backend is not enforcing (ufw inactive). A success, and the
                // ports are open anyway because nothing is filtering — but no enforced transition
                // happened, so it is not an "opened" event. The Control Panel reports the inactive
                // backend from its own probe.
                case Core.Models.FirewallOutcome.AppliedInactive:
                    logger.LogInformation(
                        "firewall {Action} for {Instance}: staged, backend {Backend} is not enforcing",
                        action, instanceName, result.Backend);
                    return FirewallPortsOutcome.Skipped;

                // Nothing to change — the desired state already held.
                case Core.Models.FirewallOutcome.NoOp:
                    return FirewallPortsOutcome.Skipped;

                // No firewall on this host (or a read-only driver). Not a failure of ours, and not
                // something to warn about on every single start.
                case Core.Models.FirewallOutcome.Unsupported:
                    logger.LogDebug(
                        "firewall {Action} for {Instance}: backend {Backend} does not support it",
                        action, instanceName, result.Backend);
                    return FirewallPortsOutcome.Skipped;

                default:
                    logger.LogWarning(
                        "firewall {Action} FAILED for {Instance} (backend {Backend}): {Detail}",
                        action, instanceName, result.Backend, result.Detail ?? "no detail");
                    return FirewallPortsOutcome.Failed;
            }
        }
        catch (FirewallException ex)
        {
            // The authority is unreachable. On an open this means the instance is coming up behind a
            // closed port, which is worth saying plainly — the server will look healthy and be
            // unreachable.
            logger.LogWarning(ex,
                "firewall {Action} for {Instance}: the kgsm-firewall authority is unreachable", action, instanceName);
            return FirewallPortsOutcome.Failed;
        }
    }
}
