using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The pure keep/stop decision behind the zombie reaper (<see cref="InstanceSupervisor.ReapOrphanedChildren"/>):
/// a <c>waitpid(pid, WNOHANG)</c> result of the pid means we reaped it, 0 means it's still alive (keep
/// watching), and a negative result (ECHILD) means it isn't our child — in both terminal cases we stop
/// tracking the pid. The syscall itself is exercised live (it cannot be faked without a real child), so this
/// covers only the branch logic.
/// </summary>
public sealed class ReaperDecisionTests
{
    [Fact]
    public void Reaped_pid_stops_tracking()
        => Assert.True(InstanceSupervisor.ShouldStopReaping(waitpidResult: 4242, pid: 4242));

    [Fact]
    public void Live_child_keeps_tracking()
        => Assert.False(InstanceSupervisor.ShouldStopReaping(waitpidResult: 0, pid: 4242));

    [Fact]
    public void Not_our_child_or_error_stops_tracking()
        => Assert.True(InstanceSupervisor.ShouldStopReaping(waitpidResult: -1, pid: 4242)); // ECHILD
}
