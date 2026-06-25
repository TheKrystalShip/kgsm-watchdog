using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The pure growth gate behind the periodic heap trim (<see cref="MemoryTrimmer.ShouldTrim"/>): after the
/// unconditional startup trim, the timer re-trims only once the working set has grown at least
/// <see cref="MemoryTrimmer.GrowthTriggerBytes"/> since the last trim — so a settled daemon just ticks and a
/// daemon that has actually grown is reclaimed within one interval. The collect/malloc_trim itself is driven
/// live (it cannot be faked), so this covers only the decision.
/// </summary>
public sealed class MemoryTrimmerTests
{
    [Fact]
    public void Flat_working_set_does_not_trim()
        => Assert.False(MemoryTrimmer.ShouldTrim(currentWorkingSet: 20L << 20, workingSetAtLastTrim: 20L << 20));

    [Fact]
    public void Small_growth_below_threshold_does_not_trim()
        => Assert.False(MemoryTrimmer.ShouldTrim(
            currentWorkingSet: (20L << 20) + MemoryTrimmer.GrowthTriggerBytes - 1,
            workingSetAtLastTrim: 20L << 20));

    [Fact]
    public void Growth_at_threshold_trims()
        => Assert.True(MemoryTrimmer.ShouldTrim(
            currentWorkingSet: (20L << 20) + MemoryTrimmer.GrowthTriggerBytes,
            workingSetAtLastTrim: 20L << 20));

    [Fact]
    public void Large_growth_trims()
        => Assert.True(MemoryTrimmer.ShouldTrim(currentWorkingSet: 64L << 20, workingSetAtLastTrim: 20L << 20));

    [Fact]
    public void Shrinking_working_set_does_not_trim()
        => Assert.False(MemoryTrimmer.ShouldTrim(currentWorkingSet: 18L << 20, workingSetAtLastTrim: 32L << 20));
}
