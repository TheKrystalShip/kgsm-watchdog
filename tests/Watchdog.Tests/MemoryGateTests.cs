using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The node-capacity check the daemon applies before every spawn. The arithmetic is pure, so these run
/// against fakes for the two engine reads (kgsm's config, a blueprint's declared figure) and the real
/// /proc/meminfo reading the host provides. The reservation-ledger tests instead pose a node of a known
/// size (<see cref="PosedGate"/>): they are about several starts composing, which needs the node's own
/// figure to hold still — and proving a memory bound by consuming memory would be self-defeating.
/// <para>
/// What is pinned here is mostly what the gate must NOT do: refuse on its own ignorance, invent a
/// requirement nobody declared, ignore an operator's cap in favour of a vendor estimate, or let a
/// reservation outlive the instance it was taken for.
/// </para>
/// </summary>
public sealed class MemoryGateTests
{
    [Fact]
    public void Disabled_gate_allows_regardless()
    {
        var gate = Gate(enabled: "false", headroom: "999999999", minRamMb: 4096);

        Assert.Equal(MemoryGate.Verdict.Allowed, gate.Evaluate(Spec(capMb: 8192)).Verdict);
    }

    [Fact]
    public void Instance_cap_is_preferred_over_the_blueprint_figure()
    {
        // A headroom that a 2048MB requirement clears but an 8192MB one would not, on any host with a
        // few gigabytes free. The cap is the cgroup ceiling the watchdog itself writes, so it bounds
        // what the node can actually lose; the blueprint's number is a vendor estimate.
        int available = ReadAvailableMb();
        var gate = Gate(enabled: "true", headroom: (available - 4096).ToString(), minRamMb: 8192);

        Assert.Equal(MemoryGate.Verdict.Allowed, gate.Evaluate(Spec(capMb: 2048)).Verdict);
    }

    [Fact]
    public void Blueprint_figure_is_used_when_the_instance_is_uncapped()
    {
        int available = ReadAvailableMb();
        var gate = Gate(enabled: "true", headroom: (available - 4096).ToString(), minRamMb: 8192);

        // 0 is kgsm's spelling of "uncapped", so this falls through to the blueprint's 8192 — which,
        // against this headroom, does not fit.
        MemoryGate.Decision decision = gate.Evaluate(Spec(capMb: 0));

        Assert.Equal(MemoryGate.Verdict.Refused, decision.Verdict);
        Assert.Contains("8192MB", decision.Reason);
    }

    [Fact]
    public void Undeclared_requirement_is_not_checked_and_not_refused()
    {
        // Neither a cap nor a blueprint figure. Even against an impossible headroom the gate allows:
        // no number was declared, and inventing one would refuse real starts on a figure nobody
        // measured. Most blueprints are uncurated, so this is the common path, not an edge case.
        var gate = Gate(enabled: "true", headroom: "999999999", minRamMb: null);

        MemoryGate.Decision decision = gate.Evaluate(Spec(capMb: 0));

        Assert.Equal(MemoryGate.Verdict.NotChecked, decision.Verdict);
        Assert.False(decision.IsRefused);
    }

    [Fact]
    public void Refusal_names_all_three_figures()
    {
        int available = ReadAvailableMb();
        var gate = Gate(enabled: "true", headroom: "999999999", minRamMb: null);

        MemoryGate.Decision decision = gate.Evaluate(Spec(capMb: 2048));

        Assert.Equal(MemoryGate.Verdict.Refused, decision.Verdict);
        // "Not enough memory" alone tells an operator nothing about whether to stop something, lower a
        // cap, or edit a blueprint. The node's own figure is asserted by SHAPE, not value: it moves
        // between the reading here and the one the gate takes.
        Assert.Contains("2048MB", decision.Reason);
        Assert.Contains("999999999MB", decision.Reason);
        Assert.Matches(@"the node has \d+MB available", decision.Reason);
        Assert.True(available > 0, "the host running these tests reports available memory");
    }

    [Fact]
    public void Missing_config_keys_leave_the_gate_ON_at_the_default_headroom()
    {
        // A host whose config predates the gate must be protected, not unprotected. Absent keys fall
        // back to the same coded defaults kgsm uses (on, 1024MB), so the two halves agree even before
        // the config migration has run.
        var gate = Gate(enabled: null, headroom: null, minRamMb: null);

        // 1MB against a 1024MB floor fits on any host that can run this suite, so the gate is proven
        // ON and permissive rather than merely absent.
        Assert.Equal(MemoryGate.Verdict.Allowed, gate.Evaluate(Spec(capMb: 1)).Verdict);
    }

    [Fact]
    public void Malformed_headroom_falls_back_rather_than_removing_the_reserve()
    {
        // A typo in config.ini must not silently disable the protection.
        var gate = Gate(enabled: "true", headroom: "not-a-number", minRamMb: null);

        Assert.Equal(MemoryGate.Verdict.Allowed, gate.Evaluate(Spec(capMb: 1)).Verdict);
    }

    [Fact]
    public void An_engine_that_throws_leaves_the_gate_at_its_defaults()
    {
        // Reading the config is how the gate is TUNED; it is not what decides the gate exists. A
        // supervisor that refused to start game servers because it could not read its own config would
        // be a worse outage than the one the gate prevents.
        var gate = new MemoryGate(new ThrowingConfig(), new FakeBlueprints(null),
            NullLogger<MemoryGate>.Instance);

        Assert.Equal(MemoryGate.Verdict.Allowed, gate.Evaluate(Spec(capMb: 1)).Verdict);
    }

    // ---- the reservation ledger -------------------------------------------------------------

    [Fact]
    public void A_second_start_is_refused_against_the_first_ones_outstanding_reservation()
    {
        // The defect this ledger exists for: MemAvailable lags a server that has just spawned, so the
        // raw reading alone lets every instance of a batch pass honestly and the node fills anyway.
        // 10000MB node, 1024MB floor, two 8192MB instances: the first fits, the second does not — but
        // the node still reads 10000MB when it is judged, because the first has allocated almost
        // nothing yet.
        var gate = PosedGate(availableMb: 10_000);

        Assert.Equal(MemoryGate.Verdict.Allowed, gate.TryReserve("first", Spec(capMb: 8192)).Verdict);

        MemoryGate.Decision second = gate.TryReserve("second", Spec(capMb: 8192));

        Assert.Equal(MemoryGate.Verdict.Refused, second.Verdict);
        Assert.Contains("8192MB committed to 1 instance(s) still starting", second.Reason);
        Assert.Equal(8192, gate.OutstandingReservedMb()); // the refused one reserved nothing
    }

    [Fact]
    public void Readiness_releases_the_reservation_and_the_same_start_is_then_allowed()
    {
        // Readiness is the release signal: from that moment the instance's memory is in the node's own
        // reading, so continuing to subtract it would double-count. The node reading is held fixed here
        // precisely so the ONLY thing that changes is the ledger.
        var gate = PosedGate(availableMb: 10_000);
        gate.TryReserve("first", Spec(capMb: 8192));
        Assert.True(gate.TryReserve("second", Spec(capMb: 8192)).IsRefused);

        gate.Release("first"); // what NativePlayerPresenceIngester.EmitReady does

        Assert.Equal(0, gate.OutstandingReservedMb());
        Assert.Equal(MemoryGate.Verdict.Allowed, gate.TryReserve("second", Spec(capMb: 8192)).Verdict);
    }

    [Fact]
    public void Releasing_twice_or_releasing_an_unknown_instance_is_a_noop()
    {
        // Release is called from several teardown paths that can overlap (a stop that lands while the
        // crash path is already concluding the same run), so it must never depend on being called once.
        var gate = PosedGate(availableMb: 10_000);
        gate.TryReserve("first", Spec(capMb: 4096));

        gate.Release("first");
        gate.Release("first");
        gate.Release("never-reserved");

        Assert.Equal(0, gate.OutstandingReservedMb());
    }

    [Fact]
    public void A_respawn_replaces_its_own_reservation_rather_than_stacking_a_second()
    {
        // A restart re-spawns an instance that may still hold a reservation from the run that just
        // ended. Two entries for one instance would reserve twice what it can ever use.
        var gate = PosedGate(availableMb: 10_000);

        gate.TryReserve("looping", Spec(capMb: 4096));
        gate.TryReserve("looping", Spec(capMb: 4096));

        Assert.Equal(4096, gate.OutstandingReservedMb());
    }

    [Fact]
    public void The_backstop_releases_a_reservation_that_never_reported_ready()
    {
        // The case with no release signal at all: a non-empty startup_success_regex that fails to
        // compile disables readiness detection, so no instance-ready event will ever arrive for this
        // instance. Without the backstop its reservation would sterilise the node for the life of the
        // daemon.
        var clock = new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var gate = PosedGate(availableMb: 10_000, clock: clock);

        gate.TryReserve("no-ready-signal", Spec(capMb: 8192));
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(gate.TryReserve("other", Spec(capMb: 8192)).IsRefused); // still held at 9 minutes

        clock.Advance(TimeSpan.FromMinutes(2)); // past the ten-minute ceiling

        Assert.Equal(0, gate.OutstandingReservedMb());
        Assert.Equal(MemoryGate.Verdict.Allowed, gate.TryReserve("other", Spec(capMb: 8192)).Verdict);
    }

    [Fact]
    public void An_unanswerable_check_still_allows_with_reservations_outstanding()
    {
        // The ledger must not turn a check the gate cannot run into a refusal. An unreadable
        // /proc/meminfo is the same answer it always was — allowed, NotChecked — however much is
        // committed to instances that are still starting.
        var gate = PosedGate(availableMb: 10_000);
        gate.TryReserve("first", Spec(capMb: 8000));

        var unreadable = PosedGate(availableMb: null);
        unreadable.TryReserve("first", Spec(capMb: 8000));

        // Nothing declared: no requirement, so nothing to judge and nothing to reserve.
        MemoryGate.Decision undeclared = gate.TryReserve("undeclared", Spec(capMb: 0));
        Assert.Equal(MemoryGate.Verdict.NotChecked, undeclared.Verdict);
        Assert.Equal(8000, gate.OutstandingReservedMb());

        // Meminfo unreadable: allowed, and again nothing reserved on a figure that was never compared.
        MemoryGate.Decision blind = unreadable.TryReserve("second", Spec(capMb: 8000));
        Assert.Equal(MemoryGate.Verdict.NotChecked, blind.Verdict);
        Assert.False(blind.IsRefused);
        Assert.Equal(0, unreadable.OutstandingReservedMb());
    }

    [Fact]
    public void A_disabled_gate_reserves_nothing()
    {
        // Off is off: with no check being made there is nothing for a ledger to compose over, and an
        // entry taken here would only wait out the backstop.
        var gate = PosedGate(availableMb: 10_000, enabled: "false");

        Assert.Equal(MemoryGate.Verdict.Allowed, gate.TryReserve("first", Spec(capMb: 8192)).Verdict);
        Assert.Equal(0, gate.OutstandingReservedMb());
    }

    // ---- forcing past the verdict -------------------------------------------------------------

    [Fact]
    public void A_forced_start_goes_ahead_and_still_takes_its_reservation()
    {
        // Force is a person's judgement that a declared figure overstates what the game really uses.
        // It goes past the VERDICT, not past the ledger: what this instance is about to take is what
        // the next one has to be judged against, whichever way its own verdict went. A forced start
        // that reserved nothing would put the staleness back exactly when the node is fullest.
        var gate = PosedGate(availableMb: 2000);

        MemoryGate.Decision forced = gate.TryReserve("forced", Spec(capMb: 4096), force: true);

        Assert.Equal(MemoryGate.Verdict.Forced, forced.Verdict);
        Assert.False(forced.IsRefused);              // the spawn proceeds
        Assert.Contains("4096MB", forced.Reason);    // …and the log still gets the figures
        Assert.Equal(4096, gate.OutstandingReservedMb());
    }

    [Fact]
    public void The_next_start_is_judged_against_what_a_forced_one_took()
    {
        // The point of reserving on a forced start: the instance after it must not be told the node
        // has room that the forced one is in the middle of claiming.
        var gate = PosedGate(availableMb: 12_000);
        gate.TryReserve("forced", Spec(capMb: 8192), force: true);

        MemoryGate.Decision next = gate.TryReserve("next", Spec(capMb: 4096));

        Assert.Equal(MemoryGate.Verdict.Refused, next.Verdict);
        Assert.Contains("8192MB committed to 1 instance(s) still starting", next.Reason);
    }

    [Fact]
    public void Force_changes_nothing_for_a_start_that_fits()
    {
        // Forcing is not a second code path: a start with room is Allowed and reserves exactly as it
        // would have, so a caller that always forces is not running different arithmetic.
        var gate = PosedGate(availableMb: 12_000);

        Assert.Equal(MemoryGate.Verdict.Allowed, gate.TryReserve("fits", Spec(capMb: 4096), force: true).Verdict);
        Assert.Equal(4096, gate.OutstandingReservedMb());
    }

    [Fact]
    public void Force_reserves_nothing_where_the_gate_could_not_answer()
    {
        // No verdict was reached, so there is nothing to force past and no measured figure to reserve.
        // Inventing one here would be the fabrication the whole gate refuses to make.
        var undeclared = PosedGate(availableMb: 2000);
        Assert.Equal(MemoryGate.Verdict.NotChecked, undeclared.TryReserve("x", Spec(capMb: 0), force: true).Verdict);
        Assert.Equal(0, undeclared.OutstandingReservedMb());

        var unreadable = PosedGate(availableMb: null);
        Assert.Equal(MemoryGate.Verdict.NotChecked, unreadable.TryReserve("x", Spec(capMb: 4096), force: true).Verdict);
        Assert.Equal(0, unreadable.OutstandingReservedMb());

        var off = PosedGate(availableMb: 2000, enabled: "false");
        Assert.Equal(MemoryGate.Verdict.Allowed, off.TryReserve("x", Spec(capMb: 4096), force: true).Verdict);
        Assert.Equal(0, off.OutstandingReservedMb());
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static int ReadAvailableMb()
    {
        foreach (string line in File.ReadLines("/proc/meminfo"))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return (int)(long.Parse(parts[1]) / 1024);
        }
        throw new InvalidOperationException("/proc/meminfo has no MemAvailable");
    }

    private static MemoryGate Gate(string? enabled, string? headroom, int? minRamMb) =>
        new(new FakeConfig(enabled, headroom), new FakeBlueprints(minRamMb), NullLogger<MemoryGate>.Instance);

    /// <summary>
    /// A gate over a POSED node reading rather than the host's own. The ledger tests are about a set of
    /// starts composing, so the node's figure has to hold still while the reservations move — and a test
    /// that proved the point by allocating memory would be the thing the gate exists to prevent.
    /// <paramref name="availableMb"/> null poses an unreadable /proc/meminfo.
    /// </summary>
    private static MemoryGate PosedGate(
        int? availableMb, string? enabled = "true", string? headroom = "1024", int? minRamMb = null,
        TestClock? clock = null) =>
        new(new FakeConfig(enabled, headroom), new FakeBlueprints(minRamMb), NullLogger<MemoryGate>.Instance,
            () => availableMb, clock ?? new TestClock(DateTime.UtcNow));

    /// <summary>A clock the test moves by hand, so the reservation backstop is exercised in microseconds.</summary>
    private sealed class TestClock(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static Instance Spec(int capMb) => new()
    {
        Name = "test-instance",
        BlueprintFile = "/blueprints/testgame.bp.yaml",
        MemoryCapMb = capMb,
    };

    private sealed class FakeConfig(string? enabled, string? headroom) : IConfigService
    {
        public string? Get(string key) => key switch
        {
            "enable_memory_gate" => enabled,
            "memory_gate_headroom_mb" => headroom,
            _ => null,
        };

        public KgsmResult Set(string key, string value) => new(0, "", "");
        public Dictionary<string, string> List() => [];
        public KgsmResult Reset() => new(0, "", "");
        public KgsmResult Validate() => new(0, "", "");
        public KgsmResult Merge() => new(0, "", "");
        public KgsmResult Rollback(int generation = 0) => new(0, "", "");
        public KgsmResult Diff(int generation = 0) => new(0, "", "");
    }

    private sealed class ThrowingConfig : IConfigService
    {
        public string? Get(string key) => throw new InvalidOperationException("engine unreachable");
        public KgsmResult Set(string key, string value) => new(0, "", "");
        public Dictionary<string, string> List() => [];
        public KgsmResult Reset() => new(0, "", "");
        public KgsmResult Validate() => new(0, "", "");
        public KgsmResult Merge() => new(0, "", "");
        public KgsmResult Rollback(int generation = 0) => new(0, "", "");
        public KgsmResult Diff(int generation = 0) => new(0, "", "");
    }

    private sealed class FakeBlueprints(int? minRamMb) : IBlueprintService
    {
        public Blueprint? GetInfo(string blueprintName) => new()
        {
            Name = blueprintName,
            Metadata = new BlueprintMetadata { MinRamMb = minRamMb },
        };

        public List<string> List() => [];
        public List<string> ListDefault() => [];
        public List<string> ListCustom() => [];
        public Dictionary<string, Blueprint> ListDetailed() => [];
        public BlueprintCandidates? FindAll(string blueprintName) => null;
        public BlueprintValidation? Validate(string blueprintNameOrPath) => null;
        public string? FindPath(string blueprintName) => null;
        public string? GetScaffold() => null;
    }
}
