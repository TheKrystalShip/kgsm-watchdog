using System.Diagnostics;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The kernel's record of when a process started, and the run name derived from it. Read against real
/// <c>/proc</c> — the daemon only ever runs on Linux, and a fake would be testing the fake.
/// </summary>
public sealed class ProcessStartClockTests
{
    [Fact]
    public void The_start_time_of_this_very_process_matches_what_the_runtime_reports()
    {
        DateTime? measured = ProcessStartClock.StartedAtUtc(Environment.ProcessId);
        Assert.NotNull(measured);

        DateTime expected = Process.GetCurrentProcess().StartTime.ToUniversalTime();

        // Both derive from the same kernel value; the clock-tick granularity of /proc's starttime and
        // the boot-time second the runtime rounds differently leave a small, bounded gap.
        Assert.True(
            Math.Abs((measured!.Value - expected).TotalSeconds) < 2,
            $"measured {measured:O} against the runtime's {expected:O}");
    }

    [Fact]
    public void A_run_key_is_stable_for_one_process_and_different_for_another()
    {
        string? mine = ProcessStartClock.RunKeyFor(Environment.ProcessId);
        Assert.NotNull(mine);
        Assert.Equal(mine, ProcessStartClock.RunKeyFor(Environment.ProcessId));

        // pid 1 exists on every Linux host and started before this test did.
        string? init = ProcessStartClock.RunKeyFor(1);
        Assert.NotNull(init);
        Assert.NotEqual(mine, init);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void A_pid_with_no_process_behind_it_is_an_honest_unknown(int pid)
    {
        Assert.Null(ProcessStartClock.RunKeyFor(pid));
        Assert.Null(ProcessStartClock.StartedAtUtc(pid));
    }
}
