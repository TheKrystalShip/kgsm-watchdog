using Microsoft.Extensions.Logging.Abstractions;
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
        new(WatchdogOptions.FromSettings(new WatchdogSettings { EnableMemoryGate = false }),
            new FakeInstanceCatalog(), NullLogger<MemoryGate>.Instance);

    /// <summary>
    /// A gate that is ON over a node of a POSED size, for the tests that assert the reservation ledger's
    /// wiring (what takes a reservation, and what releases one). Posed rather than measured because the
    /// question is which call sites move the ledger — and a test that proved a memory bound by consuming
    /// memory would be the thing the gate exists to prevent.
    /// </summary>
    public static MemoryGate Posed(int availableMb, int headroomMb = 1024) =>
        new(WatchdogOptions.FromSettings(new WatchdogSettings { EnableMemoryGate = true, MemoryGateHeadroomMb = headroomMb }),
            new FakeInstanceCatalog(), NullLogger<MemoryGate>.Instance, () => availableMb, TimeProvider.System);
}
