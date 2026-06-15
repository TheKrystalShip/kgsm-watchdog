using TheKrystalShip.KGSM.Watchdog.PortForwarding;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the two pure helpers of the UPnP path: the parser for the stored <c>upnp_ports</c> value
/// and the <c>upnpc</c> argv builder. (The shell-out itself needs a real IGD and is exercised live,
/// not in the unit suite — same boundary as <see cref="SpawnEngineTests"/>.)
/// <para>
/// The parser fixtures are pinned to the <b>verbatim</b> shape KGSM emits, captured from a real
/// deployed instance: <c>"instances info &lt;name&gt; --json"</c> returns
/// <c>"upnp_ports": "(34197 tcp 34197 udp)"</c> — parens included, alternating port/proto,
/// whitespace-separated. The format is inferred from nothing; it is the observed wire value.
/// </para>
/// </summary>
public sealed class UpnpServiceTests
{
    // ---- parser ------------------------------------------------------------------------------

    [Fact]
    public void TryParsePorts_parses_the_real_factorio_wire_value()
    {
        // The exact string observed from `kgsm instances info factorio-test --json`.
        bool ok = UpnpService.TryParsePorts("(34197 tcp 34197 udp)", out var ports, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal([(34197, "tcp"), (34197, "udp")], ports);
    }

    [Fact]
    public void TryParsePorts_parses_distinct_ports_and_protocols()
    {
        bool ok = UpnpService.TryParsePorts("(8080 tcp 8081 udp)", out var ports, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal([(8080, "tcp"), (8081, "udp")], ports);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("()")]
    [InlineData("(  )")]
    public void TryParsePorts_empty_or_parens_only_is_a_clean_noop(string? raw)
    {
        bool ok = UpnpService.TryParsePorts(raw, out var ports, out var error);

        Assert.True(ok);          // not an error — just nothing to do
        Assert.Null(error);
        Assert.Empty(ports);
    }

    [Fact]
    public void TryParsePorts_parses_without_surrounding_parens()
    {
        // Defensive: a parens-less form still parses (we only strip parens if present).
        bool ok = UpnpService.TryParsePorts("34197 udp", out var ports, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal([(34197, "udp")], ports);
    }

    [Fact]
    public void TryParsePorts_normalizes_uppercase_protocol()
    {
        bool ok = UpnpService.TryParsePorts("(27015 UDP)", out var ports, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal([(27015, "udp")], ports);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void TryParsePorts_accepts_boundary_ports(int port)
    {
        bool ok = UpnpService.TryParsePorts($"({port} tcp)", out var ports, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal([(port, "tcp")], ports);
    }

    [Fact]
    public void TryParsePorts_rejects_odd_token_count_as_a_batch()
    {
        bool ok = UpnpService.TryParsePorts("(8080 tcp 8081)", out var ports, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Empty(ports); // rejected as a batch — never a guessed partial set
    }

    [Theory]
    [InlineData("(0 tcp)")]       // below range
    [InlineData("(65536 tcp)")]   // above range
    [InlineData("(-1 tcp)")]      // negative
    [InlineData("(abc tcp)")]     // non-numeric
    public void TryParsePorts_rejects_bad_port(string raw)
    {
        bool ok = UpnpService.TryParsePorts(raw, out var ports, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Empty(ports);
    }

    [Theory]
    [InlineData("(8080 sctp)")]
    [InlineData("(8080 icmp)")]
    [InlineData("(8080 7)")]
    public void TryParsePorts_rejects_non_tcp_udp_protocol(string raw)
    {
        bool ok = UpnpService.TryParsePorts(raw, out var ports, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Empty(ports);
    }

    [Fact]
    public void TryParsePorts_rejects_a_partial_failure_without_keeping_valid_pairs()
    {
        // First pair valid, second proto bad → the whole batch is rejected (no half-open).
        bool ok = UpnpService.TryParsePorts("(8080 tcp 8081 sctp)", out var ports, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Empty(ports);
    }

    // ---- argv builder (mirrors manage.native.d/09-network.sh flag-for-flag) ------------------

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
}
