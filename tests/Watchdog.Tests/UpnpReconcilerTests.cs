using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.Model;
using TheKrystalShip.KGSM.Watchdog.PortForwarding;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the reconcile sweep's pure decision: given what the router reports it holds and what an
/// instance is configured to forward, which forwards are missing. Everything that decides whether the
/// daemon touches the router at all lives in this diff, so it is tested apart from the shell-out (the
/// upnpc invocation itself needs a real IGD and is exercised live, same boundary as
/// <see cref="UpnpServiceTests"/>).
/// </summary>
public sealed class UpnpReconcilerTests
{
    private static UpnpMapping Row(int externalPort, string protocol, string description) =>
        new(externalPort, protocol, externalPort, "192.168.1.128", description);

    [Fact]
    public void Nothing_is_missing_when_the_router_holds_every_configured_port()
    {
        List<PortMapping> configured =
        [
            new() { Start = 8211, End = 8211, Protocol = "udp" },
            new() { Start = 27015, End = 27015, Protocol = "udp" },
        ];
        List<UpnpMapping> table = [Row(8211, "udp", "Ketchup"), Row(27015, "udp", "Ketchup")];

        Assert.Empty(UpnpReconciler.MissingPorts(configured, table, "Ketchup"));
    }

    [Fact]
    public void A_dropped_forward_is_reported_while_the_surviving_one_is_not()
    {
        // The partial case, which is why the event carries the restored subset rather than the
        // instance's whole set: re-asserting everything would overstate what actually changed.
        List<PortMapping> configured =
        [
            new() { Start = 8211, End = 8211, Protocol = "udp" },
            new() { Start = 27015, End = 27015, Protocol = "udp" },
        ];
        List<UpnpMapping> table = [Row(8211, "udp", "Ketchup")];

        var missing = UpnpReconciler.MissingPorts(configured, table, "Ketchup");

        Assert.Single(missing);
        Assert.Equal(new PortMapping { Start = 27015, End = 27015, Protocol = "udp" }, missing[0]);
    }

    [Fact]
    public void An_empty_table_reports_every_configured_port_as_missing()
    {
        // What the live failure looks like: the router silently dropped the whole instance's forwards
        // while it kept running. Reachable-but-empty is a real answer; unreachable never gets here
        // (the sweep returns before diffing).
        List<PortMapping> configured = [new() { Start = 8211, End = 8211, Protocol = "udp" }];

        var missing = UpnpReconciler.MissingPorts(configured, [], "Ketchup");

        Assert.Single(missing);
        Assert.Equal(8211, missing[0].Start);
    }

    [Fact]
    public void A_row_on_the_same_port_owned_by_someone_else_does_not_count_as_ours()
    {
        // Ownership is the description tag the watchdog writes with `upnpc -e <name>`. Another host on
        // the LAN, or another instance, holding this external port is NOT this instance's forward — so
        // it reads as missing, and the re-open that follows is the daemon claiming a port it is
        // configured for rather than silently inheriting a stranger's mapping.
        List<PortMapping> configured = [new() { Start = 8211, End = 8211, Protocol = "udp" }];
        List<UpnpMapping> table = [Row(8211, "udp", "some-other-box")];

        Assert.Single(UpnpReconciler.MissingPorts(configured, table, "Ketchup"));
    }

    [Fact]
    public void Protocol_is_part_of_the_identity_so_tcp_does_not_satisfy_udp()
    {
        List<PortMapping> configured = [new() { Start = 25565, End = 25565, Protocol = "udp" }];
        List<UpnpMapping> table = [Row(25565, "tcp", "minecraft")];

        var missing = UpnpReconciler.MissingPorts(configured, table, "minecraft");

        Assert.Single(missing);
        Assert.Equal("udp", missing[0].Protocol);
    }

    [Fact]
    public void Protocol_comparison_ignores_the_case_the_router_reports()
    {
        // upnpc prints the protocol upper-case ("UDP"); the ecosystem's canonical form is lower-case.
        // A case mismatch reading as "missing" would re-open a forward that is already there on every
        // single sweep.
        List<PortMapping> configured = [new() { Start = 8211, End = 8211, Protocol = "udp" }];
        List<UpnpMapping> table = [Row(8211, "UDP", "Ketchup")];

        Assert.Empty(UpnpReconciler.MissingPorts(configured, table, "Ketchup"));
    }

    [Fact]
    public void A_wholly_dropped_range_is_reported_as_the_range_not_as_single_ports()
    {
        List<PortMapping> configured = [new() { Start = 16261, End = 16263, Protocol = "udp" }];

        var missing = UpnpReconciler.MissingPorts(configured, [], "projectzomboid");

        Assert.Single(missing);
        Assert.Equal(new PortMapping { Start = 16261, End = 16263, Protocol = "udp" }, missing[0]);
    }

    [Fact]
    public void A_range_the_router_dropped_a_hole_in_reports_only_the_hole()
    {
        // The router kept the ends of the range and lost the middle. The re-assert covers exactly the
        // gap — a contiguous run collapses back into one mapping, so the report stays in the canonical
        // range shape rather than becoming N single ports.
        List<PortMapping> configured = [new() { Start = 16261, End = 16265, Protocol = "udp" }];
        List<UpnpMapping> table =
        [
            Row(16261, "udp", "pz"), Row(16262, "udp", "pz"), Row(16265, "udp", "pz"),
        ];

        var missing = UpnpReconciler.MissingPorts(configured, table, "pz");

        Assert.Single(missing);
        Assert.Equal(new PortMapping { Start = 16263, End = 16264, Protocol = "udp" }, missing[0]);
    }

    [Fact]
    public void Gaps_are_grouped_per_protocol()
    {
        // Same port number on both protocols is two independent forwards; collapsing must not merge
        // across protocol.
        List<PortMapping> configured =
        [
            new() { Start = 25565, End = 25565, Protocol = "tcp" },
            new() { Start = 25565, End = 25565, Protocol = "udp" },
        ];

        var missing = UpnpReconciler.MissingPorts(configured, [], "minecraft");

        Assert.Equal(2, missing.Count);
        Assert.Contains(missing, m => m.Protocol == "tcp" && m.Start == 25565 && m.End == 25565);
        Assert.Contains(missing, m => m.Protocol == "udp" && m.Start == 25565 && m.End == 25565);
    }

    [Fact]
    public void An_instance_with_no_configured_ports_is_never_missing_anything()
    {
        Assert.Empty(UpnpReconciler.MissingPorts([], [], "portless"));
    }

    // ---- the table parse the sweep reads the router through ---------------------------------------

    private const string TwoOwnersList =
        "upnpc: miniupnpc library test client, version 2.3.3.\n" +
        "Found valid IGD : http://192.168.1.1:54288/6a24b755/ctl/IPConn\n" +
        "Local LAN ip address : 192.168.1.128\n" +
        "ExternalIPAddress = 95.19.50.122\n" +
        " i protocol exPort->inAddr:inPort description remoteHost leaseTime\n" +
        " 0 UDP 8211->192.168.1.128:8211 'Ketchup' '' 0\n" +
        " 1 TCP 25565->192.168.1.128:25565 'minecraft' '' 0\n";

    private const string NoIgd =
        "No IGD UPnP Device found on the network !\n" +
        "upnpc: miniupnpc library test client, version 2.3.3.\n";

    [Fact]
    public void ParseTable_keeps_every_owner_so_one_listing_answers_for_all_instances()
    {
        var table = UpnpService.ParseTable(launched: true, timedOut: false, TwoOwnersList);

        Assert.True(table.Reached);
        Assert.Equal(2, table.Mappings.Count);
        Assert.Contains(table.Mappings, m => m.Description == "Ketchup" && m.ExternalPort == 8211);
        Assert.Contains(table.Mappings, m => m.Description == "minecraft" && m.ExternalPort == 25565);
    }

    [Fact]
    public void ParseTable_an_unreachable_router_is_not_reached_never_an_empty_table()
    {
        // The distinction the whole sweep rests on. An unreadable table presented as an empty one
        // would read as "every forward expired at once" and re-open all of them on every sweep.
        Assert.False(UpnpService.ParseTable(launched: true, timedOut: false, NoIgd).Reached);
        Assert.False(UpnpService.ParseTable(launched: false, timedOut: false, "").Reached);
        Assert.False(UpnpService.ParseTable(launched: true, timedOut: true, "").Reached);
    }
}
