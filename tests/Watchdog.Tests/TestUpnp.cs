using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// A <see cref="UpnpService"/> for a fixture that needs one to exist and never has it reach a router.
/// </summary>
/// <remarks>
/// Its router health reports into a lifecycle nobody reads, and its gateway memory points at a file in
/// a directory that does not exist, so nothing a supervision test does can leave a remembered address
/// behind.
/// </remarks>
internal static class TestUpnp
{
    public static UpnpService Service() => new(
        NullLogger<UpnpService>.Instance,
        new UpnpRouterHealth(
            new RecordingJournal().Lifecycle, reconcileSeconds: 300,
            NullLogger<UpnpRouterHealth>.Instance, static () => DateTimeOffset.UtcNow),
        new UpnpGatewayMemory(
            Path.Combine(Path.GetTempPath(), "kgsm-wd-upnp-" + Guid.NewGuid().ToString("N"), StatePathResolverFile),
            NullLogger<UpnpGatewayMemory>.Instance));

    private const string StatePathResolverFile = Supervision.StatePathResolver.UpnpGatewayFile;
}
