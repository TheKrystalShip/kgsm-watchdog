using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Firewall.Contracts;
using Microsoft.Extensions.Logging;
namespace TheKrystalShip.KGSM.Core.Models;

/// <summary>
/// Options for the kgsm-firewall control client (<c>Interfaces.IFirewallService</c>). The firewall
/// authority is a separate socket-activated daemon from kgsm and the watchdog, so its connection is
/// configured independently.
/// </summary>
public class FirewallClientOptions
{
    /// <summary>
    /// Path to the kgsm-firewall control unix-domain socket. Defaults to the daemon's own default
    /// (<c>KGSM_FIREWALL_SOCKET</c> / <c>/run/kgsm-firewall/firewall.sock</c>). The socket's filesystem
    /// permissions are the security boundary — the client performs no in-band authentication.
    /// </summary>
    public string SocketPath { get; set; } = "/run/kgsm-firewall/firewall.sock";

    /// <summary>
    /// Per-request timeout. The default matches the authority's own bundled-client timeout, which covers a
    /// ufw mutation serialised behind the global ufw lock when several instances come up at once.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
