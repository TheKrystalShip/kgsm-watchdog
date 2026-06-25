using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the ADOPTED handle (re-attach after a daemon bounce) and the load-bearing disposal split:
/// daemon shutdown must RELEASE the handle while KEEPING the FIFO (so a live game's stdin survives for the
/// next daemon to re-open), whereas a real teardown removes it. Pure (no fork/cgroup): the fd is -1, so no
/// syscall is exercised — only the handle's own bookkeeping. The FIFO node is stood in by a temp file.
/// </summary>
public sealed class RunningInstanceTests
{
    [Fact]
    public void Adopted_handle_has_recovered_pid_and_no_exit_code()
    {
        var adopted = RunningInstance.Adopt(
            "factorio-test", fifoFd: -1, fifoPath: "/nonexistent",
            stopCommand: "/quit", stopTimeoutSeconds: 5, pid: 4242, NullLogger<RunningInstance>.Instance);

        Assert.Equal(4242, adopted.Pid);   // recovered from the cgroup, not a child Process
        Assert.Null(adopted.ExitCode);     // non-child: no waitpid, so never a (fabricated) exit code
        Assert.Equal("/quit", adopted.StopCommand);
        Assert.Equal(5, adopted.StopTimeoutSeconds);
    }

    [Fact]
    public void ReleaseKeepingFifo_preserves_the_node_but_Dispose_removes_it()
    {
        string fifoPath = Path.Combine(Path.GetTempPath(), $"wd-fifo-{Guid.NewGuid():N}");
        File.WriteAllText(fifoPath, ""); // stand-in for the FIFO node the running game still reads
        try
        {
            // Daemon shutdown path: the game keeps running, so its FIFO MUST survive (this is the bug fix —
            // the old Dispose-on-shutdown deleted it and orphaned the channel).
            var onShutdown = RunningInstance.Adopt(
                "x", -1, fifoPath, "/quit", 5, pid: 1, NullLogger<RunningInstance>.Instance);
            onShutdown.ReleaseKeepingFifo();
            Assert.True(File.Exists(fifoPath), "daemon shutdown must NOT delete a live instance's FIFO");

            // Real teardown path (stop/crash/respawn): the game is gone, so the dead FIFO is cleaned up.
            var onTeardown = RunningInstance.Adopt(
                "x", -1, fifoPath, "/quit", 5, pid: 1, NullLogger<RunningInstance>.Instance);
            onTeardown.Dispose();
            Assert.False(File.Exists(fifoPath), "a real teardown (Dispose) removes the FIFO node");
        }
        finally
        {
            if (File.Exists(fifoPath)) File.Delete(fifoPath);
        }
    }
}
