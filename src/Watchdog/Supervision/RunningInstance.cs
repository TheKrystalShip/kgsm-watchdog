using Microsoft.Extensions.Logging;
using System.Diagnostics;
using TheKrystalShip.KGSM.Watchdog.Interop;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// A live supervised instance: the daemon's handle on one game process. Holds three things the
/// supervisor must keep for the instance's whole lifetime:
/// <list type="bullet">
/// <item>the child <see cref="Process"/> — keeps the daemon its parent so it can reap it and (Inc 2)
/// learn its exit; dropping it would orphan a zombie and lose the exit signal;</item>
/// <item>the FIFO keepalive <see cref="_fifoFd"/> — an O_RDWR fd the daemon holds open so the game's
/// stdin never hits EOF, and the channel it writes stop/console commands into;</item>
/// <item>the <see cref="DesiredRunning"/> intent — what the daemon was last told to do, the signal
/// Inc 2 uses to tell a crash (populated→0 while desired-running) from a deliberate stop.</item>
/// </list>
/// </summary>
internal sealed class RunningInstance : IDisposable
{
    private readonly int _fifoFd;
    private readonly string _fifoPath;
    private readonly ILogger _log;
    private bool _disposed;

    public RunningInstance(
        string name,
        Process process,
        int fifoFd,
        string fifoPath,
        string stopCommand,
        int stopTimeoutSeconds,
        ILogger log)
    {
        Name = name;
        Process = process;
        _fifoFd = fifoFd;
        _fifoPath = fifoPath;
        StopCommand = stopCommand;
        StopTimeoutSeconds = stopTimeoutSeconds;
        _log = log;
        DesiredRunning = true;
    }

    public string Name { get; }
    public Process Process { get; }

    /// <summary>The instance's graceful stop command (written to the FIFO on stop); may be empty.</summary>
    public string StopCommand { get; }

    /// <summary>How long to wait for a graceful drain before <c>cgroup.kill</c> (KGSM's <c>stop_command_timeout_seconds</c>).</summary>
    public int StopTimeoutSeconds { get; }

    /// <summary>The last intent the daemon was given. Set false by a deliberate <c>stop</c>.</summary>
    public bool DesiredRunning { get; set; }

    /// <summary>The spawned leader PID, or null if the process object is already gone.</summary>
    public int? Pid
    {
        get
        {
            try { return Process.HasExited ? null : Process.Id; }
            catch { return null; }
        }
    }

    /// <summary>
    /// Write a line into the instance's stdin FIFO (the stop command, or — later — a console
    /// command). Appends a newline to match the bash native path's <c>echo "$cmd" &gt;&gt; fifo</c>.
    /// </summary>
    public void SendLine(string line)
    {
        if (_disposed)
            return;
        byte[] buf = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        nint written = NativeMethods.write(_fifoFd, buf, (nuint)buf.Length);
        if (written < 0)
        {
            int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            _log.LogWarning("write to {Instance} FIFO failed (errno {Err})", Name, err);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_fifoFd >= 0)
            NativeMethods.close(_fifoFd);

        // Remove the FIFO file (the cgroup teardown is the supervisor's job, not the handle's).
        try { if (File.Exists(_fifoPath)) File.Delete(_fifoPath); }
        catch (Exception ex) { _log.LogDebug(ex, "could not remove FIFO {Path}", _fifoPath); }

        try { Process.Dispose(); }
        catch { /* already gone */ }
    }
}
