using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the watchdog's one pure UPnP helper: the <c>upnpc</c> argv builder, which mirrors the
/// management script (<c>manage.native.d/09-network.sh</c>) flag-for-flag. Parsing/validating the
/// port spec now lives in kgsm-lib (the canonical structured <see cref="PortMapping"/> form, tested
/// there) — the watchdog only expands it (<see cref="PortMappingExtensions.Expand"/>) and builds the
/// command line. The shell-out itself needs a real IGD and is exercised live, not in the unit suite
/// (same boundary as <see cref="SpawnEngineTests"/>).
/// </summary>
public sealed class UpnpServiceTests
{
    [Fact]
    public void BuildUpnpcArgs_open_mirrors_upnpc_dash_e_dash_r()
    {
        var args = UpnpService.BuildUpnpcArgs(
            open: true, "factorio-test", [(34197, "tcp"), (34197, "udp")]);

        // upnpc -e factorio-test -r 34197 tcp 34197 udp
        Assert.Equal(
            ["-e", "factorio-test", "-r", "34197", "tcp", "34197", "udp"], args);
    }

    [Fact]
    public void BuildUpnpcArgs_close_mirrors_upnpc_dash_f()
    {
        var args = UpnpService.BuildUpnpcArgs(
            open: false, "factorio-test", [(34197, "tcp"), (34197, "udp")]);

        // upnpc -f 34197 tcp 34197 udp
        Assert.Equal(["-f", "34197", "tcp", "34197", "udp"], args);
    }

    [Fact]
    public void BuildUpnpcArgs_from_an_expanded_range_unrolls_each_port()
    {
        // The full structured -> upnpc path: a range PortMapping expands to one upnpc arg pair per
        // port (upnpc -r takes individual external ports, not ranges).
        List<PortMapping> ports = [new() { Start = 27015, End = 27017, Protocol = "udp" }];

        var args = UpnpService.BuildUpnpcArgs(open: true, "rust-01", [.. ports.Expand()]);

        Assert.Equal(
            ["-e", "rust-01", "-r", "27015", "udp", "27016", "udp", "27017", "udp"], args);
    }

    [Fact]
    public void BuildUpnpcArgs_open_with_no_ports_is_just_the_description_and_flag()
    {
        // Defensive shape only — ApplyAsync no-ops on an empty port set before this is reached.
        var args = UpnpService.BuildUpnpcArgs(open: true, "empty-01", []);

        Assert.Equal(["-e", "empty-01", "-r"], args);
    }
}
