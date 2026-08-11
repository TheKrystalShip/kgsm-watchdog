namespace TheKrystalShip.KGSM.Watchdog.PortForwarding;

/// <summary>
/// Answers "which forwarded ports would still be wanted if this instance released its own?" — the one
/// question a UPnP close has to ask before it deletes anything.
/// <para>
/// A UPnP mapping is addressed by external port alone: <c>upnpc -f &lt;port&gt; &lt;proto&gt;</c> carries no
/// owner, and the IGD holds one row per (port, protocol) however many instances declare it. So a stop
/// that deletes its own ports deletes an identical row a still-running sibling depends on, and the
/// sibling is unreachable from outside until something puts it back. Retention is what makes the last
/// instance standing the one that releases the port.
/// </para>
/// <para>
/// It is a read-only query on desired-state, which is why it is an interface rather than a supervisor
/// reference: the container lifecycle ingester needs the answer without becoming a client of
/// supervision.
/// </para>
/// </summary>
internal interface IForwardedPortClaims
{
    /// <summary>
    /// The expanded <c>(port, protocol)</c> pairs that instances OTHER than <paramref name="excluding"/>
    /// currently want forwarded, protocol lower-cased.
    /// <para>
    /// The predicate is <em>desired-running</em>, deliberately not "has a populated cgroup": a forward is
    /// held across a crash-restart precisely because a dead process does not drop a router lease and the
    /// respawn still needs it. Retaining only for instances whose cgroup happens to be populated at this
    /// instant would delete a shared port out from under an instance that is mid-restart.
    /// </para>
    /// </summary>
    IReadOnlySet<(int Port, string Protocol)> ForwardedPortsHeldByOthers(string excluding);
}
