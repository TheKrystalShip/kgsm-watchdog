using Microsoft.Extensions.Logging.Abstractions;
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
    /// <summary>Nothing else claims these ports — the close is free to release its whole declared set.</summary>
    private static readonly IReadOnlySet<(int Port, string Protocol)> NoClaims =
        new HashSet<(int, string)>();

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

    // The two gate paths return BEFORE shelling upnpc, so they are unit-testable (unlike the
    // exit-code → Applied/Failed mapping, which needs a real IGD and is exercised live). They prove
    // the inert-by-default contract: a disabled instance (the default) does nothing → Skipped → the
    // supervisor emits no event. A non-Applied outcome is exactly what gates the audit event.

    [Fact]
    public async Task OpenAsync_returns_Skipped_when_port_forwarding_disabled()
    {
        var svc = new UpnpService(NullLogger<UpnpService>.Instance);
        // Disabled even though ports ARE configured — the gate short-circuits before any upnpc call.
        var instance = new Instance
        {
            Name = "factorio-test",
            EnablePortForwarding = false,
            Ports = [new PortMapping { Start = 34197, End = 34197, Protocol = "udp" }],
        };

        Assert.Equal(UpnpOutcome.Skipped, await svc.OpenAsync(instance));
        Assert.Equal(UpnpOutcome.Skipped, await svc.CloseAsync(instance, NoClaims));
    }

    // ---- a close never deletes a port something else is still running on --------------------------

    [Fact]
    public void Excluding_drops_the_ports_another_instance_still_claims()
    {
        // Ketchup declares 8211 and 27015; a sibling on 27015 is still running. Only 8211 is Ketchup's
        // to release — deleting 27015 would take the sibling off the air, because `upnpc -f` addresses
        // a mapping by port alone and the IGD holds one row for both of them.
        List<(int Port, string Protocol)> ports = [(8211, "udp"), (27015, "udp")];
        var retain = new HashSet<(int, string)> { (27015, "udp") };

        Assert.Equal([(8211, "udp")], PortSets.Excluding(ports, retain));
    }

    [Fact]
    public void Excluding_matches_a_claim_whatever_case_the_protocol_arrives_in()
    {
        List<(int Port, string Protocol)> ports = [(27015, "UDP")];
        var retain = new HashSet<(int, string)> { (27015, "udp") };

        Assert.Empty(PortSets.Excluding(ports, retain));
    }

    [Fact]
    public void Excluding_keeps_a_port_claimed_on_the_other_protocol()
    {
        // Protocol is part of a port's identity: a sibling holding 21025/tcp says nothing about 21025/udp.
        List<(int Port, string Protocol)> ports = [(21025, "udp")];
        var retain = new HashSet<(int, string)> { (21025, "tcp") };

        Assert.Equal([(21025, "udp")], PortSets.Excluding(ports, retain));
    }

    [Fact]
    public async Task CloseAsync_skips_without_touching_the_router_when_every_port_is_still_claimed()
    {
        // Nothing left to release means nothing changed on the router, so this is Skipped and NOT
        // Applied — an "upnp closed" event here would record a removal that never happened. It also
        // returns before upnpc is ever spawned, which is what keeps this test off a live IGD.
        var svc = new UpnpService(NullLogger<UpnpService>.Instance);
        var instance = new Instance
        {
            Name = "stationeers",
            EnablePortForwarding = true,
            Ports = [new PortMapping { Start = 27015, End = 27015, Protocol = "udp" }],
        };

        var retain = new HashSet<(int, string)> { (27015, "udp") };

        Assert.Equal(UpnpOutcome.Skipped, await svc.CloseAsync(instance, retain));
    }

    [Fact]
    public async Task OpenAsync_returns_Skipped_when_enabled_but_no_ports()
    {
        var svc = new UpnpService(NullLogger<UpnpService>.Instance);
        // Enabled but nothing to forward → a clean no-op (no upnpc call), so Skipped (no event).
        var instance = new Instance
        {
            Name = "no-ports-01",
            EnablePortForwarding = true,
            Ports = [],
        };

        Assert.Equal(UpnpOutcome.Skipped, await svc.OpenAsync(instance));
    }

    [Fact]
    public async Task OpenAsync_explicit_ports_still_gated_off_when_forwarding_disabled()
    {
        var svc = new UpnpService(NullLogger<UpnpService>.Instance);
        // Config is the authority: an external explicit-port open does NOT force-forward a gated-off
        // instance — the gate short-circuits before any upnpc call → Skipped (no event).
        var instance = new Instance { Name = "gated-01", EnablePortForwarding = false, Ports = [] };
        IReadOnlyList<PortMapping> ports = [new() { Start = 39999, End = 39999, Protocol = "udp" }];

        Assert.Equal(UpnpOutcome.Skipped, await svc.OpenAsync(instance, ports));
    }

    // ---- ListAsync output parsing (the tolerant, human-format parse of `upnpc -l`) -----------------
    // Captured verbatim from miniupnpc 2.3.3 on the live host, so the parser is tested against real
    // output, not a guessed shape. The shell-out itself needs a real IGD and is exercised live.

    // A real `upnpc -l` against a reachable IGD that currently holds one mapping (external port 39999
    // UDP, described "upnp-parser-test" — the instance-name ownership tag we set with `-e` on open).
    private const string PopulatedList =
        "upnpc: miniupnpc library test client, version 2.3.3.\n" +
        " (c) 2005-2025 Thomas Bernard.\n" +
        "List of UPNP devices found on the network :\n" +
        " desc: http://192.168.1.1:51053/8ea8ac0f/rootDesc.xml\n" +
        " st: urn:schemas-upnp-org:device:InternetGatewayDevice:1\n" +
        "\n" +
        "Found valid IGD : http://192.168.1.1:51053/8ea8ac0f/ctl/IPConn\n" +
        "Local LAN ip address : 192.168.1.128\n" +
        "ExternalIPAddress = 95.19.50.122\n" +
        " i protocol exPort->inAddr:inPort description remoteHost leaseTime\n" +
        " 0 UDP 39999->192.168.1.128:39999 'upnp-parser-test' '' 0\n";

    // A real IGD reached, but the redirection table is empty (no mappings owned by anyone).
    private const string EmptyList =
        "upnpc: miniupnpc library test client, version 2.3.3.\n" +
        "Found valid IGD : http://192.168.1.1:51053/8ea8ac0f/ctl/IPConn\n" +
        "Local LAN ip address : 192.168.1.128\n" +
        "ExternalIPAddress = 95.19.50.122\n" +
        " i protocol exPort->inAddr:inPort description remoteHost leaseTime\n";

    // No router on the network — upnpc prints this AND exits 0 (so exit code is not a usable signal).
    private const string NoIgd =
        "No IGD UPnP Device found on the network !\n" +
        "upnpc: miniupnpc library test client, version 2.3.3.\n" +
        " (c) 2005-2025 Thomas Bernard.\n";

    [Fact]
    public void ParseListOutput_populated_returns_the_owned_mapping()
    {
        var result = UpnpService.ParseListOutput("upnp-parser-test", launched: true, timedOut: false, PopulatedList);

        Assert.Equal("queried", result.State);
        var m = Assert.Single(result.Mappings);
        Assert.Equal(39999, m.ExternalPort);
        Assert.Equal("udp", m.Protocol);            // lower-cased to the ecosystem convention
        Assert.Equal(39999, m.InternalPort);
        Assert.Equal("192.168.1.128", m.InternalClient);
        Assert.Equal("upnp-parser-test", m.Description);
    }

    [Fact]
    public void ParseListOutput_filters_out_rows_owned_by_other_instances()
    {
        // Same populated output, but we ask for a DIFFERENT instance — its mapping is not ours, so an
        // honest empty (queried) list, never another instance's row.
        var result = UpnpService.ParseListOutput("some-other-instance", launched: true, timedOut: false, PopulatedList);

        Assert.Equal("queried", result.State);
        Assert.Empty(result.Mappings);
    }

    [Fact]
    public void ParseListOutput_reachable_but_empty_is_queried_none_not_unavailable()
    {
        // The router answered ("Found valid IGD") but owns no mappings → a genuine "none", distinct
        // from an inability to ask.
        var result = UpnpService.ParseListOutput("factorio-test", launched: true, timedOut: false, EmptyList);

        Assert.Equal("queried", result.State);
        Assert.Empty(result.Mappings);
    }

    [Fact]
    public void ParseListOutput_no_igd_is_unavailable_never_a_fabricated_empty()
    {
        // upnpc exited 0 but found no IGD — this MUST read as "couldn't ask", not "no forwards".
        var result = UpnpService.ParseListOutput("factorio-test", launched: true, timedOut: false, NoIgd);

        Assert.Equal("unavailable", result.State);
        Assert.Empty(result.Mappings);
    }

    [Fact]
    public void ParseListOutput_launch_failure_and_timeout_are_unavailable()
    {
        Assert.Equal("unavailable",
            UpnpService.ParseListOutput("x", launched: false, timedOut: false, "").State);
        Assert.Equal("unavailable",
            UpnpService.ParseListOutput("x", launched: true, timedOut: true, "").State);
    }

    [Theory]
    [InlineData(" i protocol exPort->inAddr:inPort description remoteHost leaseTime")] // the table header
    [InlineData("Found valid IGD : http://192.168.1.1:51053/8ea8ac0f/ctl/IPConn")]     // a banner line
    [InlineData(" desc: http://192.168.1.1:51053/8ea8ac0f/rootDesc.xml")]              // device discovery
    [InlineData("")]                                                                    // blank
    public void TryParseMappingRow_rejects_non_redirection_lines(string line)
    {
        Assert.False(UpnpService.TryParseMappingRow(line, out _));
    }

    [Fact]
    public void TryParseMappingRow_parses_a_tcp_row()
    {
        Assert.True(UpnpService.TryParseMappingRow(
            " 3 TCP 27015->192.168.1.50:27015 'my server' 'remote.example' 3600", out var m));
        Assert.Equal(27015, m.ExternalPort);
        Assert.Equal("tcp", m.Protocol);
        Assert.Equal("192.168.1.50", m.InternalClient);
        Assert.Equal(27015, m.InternalPort);
        Assert.Equal("my server", m.Description); // a space inside the quoted description is preserved
    }
}
