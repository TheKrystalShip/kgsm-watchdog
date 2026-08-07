using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Exceptions;
using TheKrystalShip.KGSM.Watchdog.Firewall;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the host-firewall side effect the supervisor hangs off a bring-up and a deliberate stop: the
/// per-instance gate, the no-ports skip, and the mapping from the authority's reply onto the three-token
/// outcome the supervisor uses to decide whether an audit event is honest.
/// <para>
/// The distinction under test throughout is <b>Applied vs. everything else</b>. Only a confirmed rule
/// change may produce an <c>instance-ports-opened</c>/<c>-closed</c> event; a staged-but-unenforced rule,
/// a backend that cannot do it, and an authority that cannot be reached must all fail to produce one,
/// because each of them means the ports are not measurably open.
/// </para>
/// </summary>
public sealed class FirewallPortsServiceTests
{
    private static Instance Native(bool firewallManaged, params PortMapping[] ports) => new()
    {
        Name = "fw-test",
        Runtime = InstanceRuntime.Native,
        EnableFirewallManagement = firewallManaged,
        Ports = [.. ports],
    };

    private static PortMapping Port(int port, string proto = "tcp")
        => new() { Start = port, End = port, Protocol = proto };

    private static FirewallPortsService NewService(FakeFirewall fake)
        => new(fake, NullLogger<FirewallPortsService>.Instance);

    // ---- the per-instance gate ---------------------------------------------------------------

    [Fact]
    public async Task Open_is_skipped_and_never_reaches_the_authority_when_management_is_off()
    {
        // `files firewall disable` is an explicit "stop managing this instance". A bring-up must not
        // quietly undo it, so the authority is not even asked.
        var fake = new FakeFirewall();
        var outcome = await NewService(fake).OpenAsync(Native(firewallManaged: false, Port(25565)));

        Assert.Equal(FirewallPortsOutcome.Skipped, outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Close_is_skipped_and_never_reaches_the_authority_when_management_is_off()
    {
        var fake = new FakeFirewall();
        var outcome = await NewService(fake).CloseAsync(Native(firewallManaged: false, Port(25565)));

        Assert.Equal(FirewallPortsOutcome.Skipped, outcome);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Open_with_no_declared_ports_skips_rather_than_owning_an_empty_rule_set()
    {
        var fake = new FakeFirewall();
        var outcome = await NewService(fake).OpenAsync(Native(firewallManaged: true));

        Assert.Equal(FirewallPortsOutcome.Skipped, outcome);
        Assert.Empty(fake.Calls);
    }

    // ---- the happy paths ---------------------------------------------------------------------

    [Fact]
    public async Task Open_hands_the_instances_ports_to_the_authority_verbatim()
    {
        var fake = new FakeFirewall { Next = new FirewallActionResult { Ok = true, Outcome = FirewallOutcome.Applied } };

        var outcome = await NewService(fake).OpenAsync(
            Native(firewallManaged: true, Port(25565, "tcp"), Port(25565, "udp")));

        Assert.Equal(FirewallPortsOutcome.Applied, outcome);
        var call = Assert.Single(fake.Calls);
        Assert.Equal("ensure-open", call.Verb);
        Assert.Equal("fw-test", call.Instance);
        Assert.Equal(["25565/tcp", "25565/udp"], call.Ports);
    }

    [Fact]
    public async Task Close_removes_by_ownership_tag_even_for_an_instance_with_no_ports()
    {
        // Removal is addressed by name, so it still cleans up an instance whose declared ports were
        // emptied after the rule was written.
        var fake = new FakeFirewall { Next = new FirewallActionResult { Ok = true, Outcome = FirewallOutcome.Removed } };

        var outcome = await NewService(fake).CloseAsync(Native(firewallManaged: true));

        Assert.Equal(FirewallPortsOutcome.Applied, outcome);
        var call = Assert.Single(fake.Calls);
        Assert.Equal("remove", call.Verb);
        Assert.Equal("fw-test", call.Instance);
    }

    // ---- everything that must NOT produce an event -------------------------------------------

    [Fact]
    public async Task A_staged_rule_on_an_inactive_backend_is_not_an_opened_event()
    {
        // ufw is installed but disabled: the rule persists and takes effect on the next `ufw enable`.
        // The port is reachable meanwhile because nothing filters — but no enforced open happened, and
        // claiming one would describe a state the authority never entered.
        var fake = new FakeFirewall
        {
            Next = new FirewallActionResult { Ok = true, Outcome = FirewallOutcome.AppliedInactive, Backend = "ufw" },
        };

        var outcome = await NewService(fake).OpenAsync(Native(firewallManaged: true, Port(25565)));

        Assert.Equal(FirewallPortsOutcome.Skipped, outcome);
    }

    [Fact]
    public async Task A_noop_is_not_an_opened_event()
    {
        var fake = new FakeFirewall { Next = new FirewallActionResult { Ok = true, Outcome = FirewallOutcome.NoOp } };

        Assert.Equal(FirewallPortsOutcome.Skipped,
            await NewService(fake).OpenAsync(Native(firewallManaged: true, Port(25565))));
    }

    [Fact]
    public async Task A_backend_that_cannot_do_it_is_skipped_not_failed()
    {
        // A host with no firewall at all. Nothing is wrong, and warning on every start would be noise.
        var fake = new FakeFirewall
        {
            Next = new FirewallActionResult { Ok = false, Outcome = FirewallOutcome.Unsupported, Backend = "none" },
        };

        Assert.Equal(FirewallPortsOutcome.Skipped,
            await NewService(fake).OpenAsync(Native(firewallManaged: true, Port(25565))));
    }

    [Fact]
    public async Task A_rejected_rule_is_failed()
    {
        var fake = new FakeFirewall
        {
            Next = new FirewallActionResult { Ok = false, Outcome = FirewallOutcome.Failed, Backend = "ufw", Detail = "rejected" },
        };

        Assert.Equal(FirewallPortsOutcome.Failed,
            await NewService(fake).OpenAsync(Native(firewallManaged: true, Port(25565))));
    }

    [Fact]
    public async Task An_unreachable_authority_is_failed_and_never_escapes_as_an_exception()
    {
        // The client signals unreachable by throwing. If that escaped, it would fault the supervisor's
        // fire-and-forget task instead of being reported as "the ports were not opened".
        var fake = new FakeFirewall { Throw = new FirewallException("no socket") };

        Assert.Equal(FirewallPortsOutcome.Failed,
            await NewService(fake).OpenAsync(Native(firewallManaged: true, Port(25565))));
        Assert.Equal(FirewallPortsOutcome.Failed,
            await NewService(fake).CloseAsync(Native(firewallManaged: true, Port(25565))));
    }

    /// <summary>
    /// An <see cref="IFirewallService"/> that records what it was asked for and answers with a canned
    /// result (or throws the unreachable signal). Read-only members the service never calls throw.
    /// </summary>
    internal sealed class FakeFirewall : IFirewallService
    {
        internal sealed record Call(string Verb, string Instance, string[] Ports);

        public List<Call> Calls { get; } = [];
        public FirewallActionResult Next { get; set; } = new() { Ok = true, Outcome = FirewallOutcome.Applied };
        public FirewallException? Throw { get; set; }

        public Task<FirewallActionResult> EnsureOpenAsync(
            string instanceName, IReadOnlyList<PortMapping> ports, CancellationToken cancellationToken = default)
        {
            if (Throw is not null) throw Throw;
            Calls.Add(new Call("ensure-open", instanceName,
                [.. ports.Select(p => p.Start == p.End ? $"{p.Start}/{p.Protocol}" : $"{p.Start}:{p.End}/{p.Protocol}")]));
            return Task.FromResult(Next);
        }

        public Task<FirewallActionResult> RemoveAsync(string instanceName, CancellationToken cancellationToken = default)
        {
            if (Throw is not null) throw Throw;
            Calls.Add(new Call("remove", instanceName, []));
            return Task.FromResult(Next);
        }

        public Task<FirewallListResult> ListOwnedAsync(string? instanceName = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<FirewallBackendInfo> BackendAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public void Dispose() { }
    }
}
