using System.Text.Json.Serialization;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// The IGD's whole redirection table, read once for a reconcile sweep. <see cref="Reached"/> carries the
/// same honesty as <see cref="UpnpListResult.State"/>: false means the router could not be asked, which
/// is NOT the same as an empty table and must never be acted on as "nothing is mapped" — a router outage
/// would otherwise read as every forward having expired at once.
/// <para>
/// <see cref="LocalAddress"/> is this host's LAN address as the IGD itself reports it on the same
/// listing. It is what makes a row's <em>target</em> checkable: a mapping pointing at this host on the
/// port it forwards is the mapping this daemon would open, whichever instance's tag it carries. Null
/// when the listing did not report one, and a null must fall back to matching on the tag alone rather
/// than assuming an address.
/// </para>
/// </summary>
internal sealed record UpnpTable(
    bool Reached, IReadOnlyList<UpnpMapping> Mappings, string? LocalAddress = null);

/// <summary>
/// A running instance the UPnP reconcile sweep is answerable for, paired with the spawn config the
/// sweep reads its ports and forwarding gate from.
/// </summary>
public sealed record ForwardingCandidate(string Name, Instance Spec);

