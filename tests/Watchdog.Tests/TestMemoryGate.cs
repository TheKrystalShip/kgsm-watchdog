using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// A <see cref="MemoryGate"/> for the supervision tests, which are about crash handling, hot-swap and
/// teardown rather than node capacity.
/// </summary>
/// <remarks>
/// It reports the gate <b>switched off</b>, which is the honest way to say "not what this test is
/// about": the supervisor then behaves exactly as it did before a gate existed, so a capacity refusal
/// can never be the hidden reason one of these tests fails. The gate's own behaviour is pinned in
/// <see cref="MemoryGateTests"/>.
/// </remarks>
internal static class TestMemoryGate
{
    public static MemoryGate Disabled() =>
        new(new DisabledConfig(), new NoBlueprints(), NullLogger<MemoryGate>.Instance);

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
