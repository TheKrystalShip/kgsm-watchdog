using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The node-capacity check the daemon applies before every spawn. The arithmetic is pure, so these run
/// against fakes for the two engine reads (kgsm's config, a blueprint's declared figure) and the real
/// /proc/meminfo reading the host provides.
/// <para>
/// What is pinned here is mostly what the gate must NOT do: refuse on its own ignorance, invent a
/// requirement nobody declared, or ignore an operator's cap in favour of a vendor estimate.
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
