using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// <see cref="MemoryGate"/>s for the tests that are not about the gate itself.
/// </summary>
/// <remarks>
/// <see cref="Disabled"/> reports the gate <b>switched off</b>, which is the honest way to say "not what
/// this test is about": the supervisor then behaves exactly as it does with no gate configured, so a
/// capacity refusal can never be the hidden reason a crash-handling or hot-swap test fails.
/// <see cref="Posed"/> is the opposite — a gate that is ON over a node of a known size, for the tests
/// that assert which call sites move the reservation ledger. The gate's own arithmetic is pinned in
/// <see cref="MemoryGateTests"/>.
/// </remarks>
internal static class TestMemoryGate
{
    public static MemoryGate Disabled() =>
        new(new DisabledConfig(), new NoBlueprints(), NullLogger<MemoryGate>.Instance);

    /// <summary>
    /// A gate that is ON over a node of a POSED size, for the tests that assert the reservation ledger's
    /// wiring (what takes a reservation, and what releases one). Posed rather than measured because the
    /// question is which call sites move the ledger — and a test that proved a memory bound by consuming
    /// memory would be the thing the gate exists to prevent.
    /// </summary>
    public static MemoryGate Posed(int availableMb, int headroomMb = 1024) =>
        new(new PosedConfig(headroomMb), new NoBlueprints(), NullLogger<MemoryGate>.Instance,
            () => availableMb, TimeProvider.System);

    private sealed class PosedConfig(int headroomMb) : IConfigService
    {
        public string? Get(string key) => key switch
        {
            "enable_memory_gate" => "true",
            "memory_gate_headroom_mb" => headroomMb.ToString(),
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

    private sealed class DisabledConfig : IConfigService
    {
        public string? Get(string key) => key == "enable_memory_gate" ? "false" : null;
        public KgsmResult Set(string key, string value) => new(0, "", "");
        public Dictionary<string, string> List() => [];
        public KgsmResult Reset() => new(0, "", "");
        public KgsmResult Validate() => new(0, "", "");
        public KgsmResult Merge() => new(0, "", "");
        public KgsmResult Rollback(int generation = 0) => new(0, "", "");
        public KgsmResult Diff(int generation = 0) => new(0, "", "");
    }

    private sealed class NoBlueprints : IBlueprintService
    {
        public Blueprint? GetInfo(string blueprintName) => null;
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
