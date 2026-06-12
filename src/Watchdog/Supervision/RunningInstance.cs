using Microsoft.Extensions.Logging;
using System.Diagnostics;
using TheKrystalShip.KGSM.Watchdog.Interop;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// A live supervised instance: the daemon's handle on one game process, valid only while the game is
/// up. Holds two things the daemon must keep for the process's whole lifetime:
/// <list type="bullet">
/// <item>the child <see cref="Process"/> — keeps the daemon its parent so it can reap it and learn its
/// exit code (the clean-vs-crash discriminator); dropping it would orphan a zombie and lose the exit
/// signal;</item>
/// <item>the FIFO keepalive <see cref="_fifoFd"/> — an O_RDWR fd the daemon holds open so the game's
/// stdin never hits EOF, and the channel it writes stop/console commands into.</item>
/// </list>
/// Desired-state and the restart counter do <em>not</em> live here — they must survive this handle's
/// disposal across a respawn, so they live on the durable <see cref="SupervisedInstance"/>.
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
    }

    public string Name { get; }
    public Process Process { get; }

    /// <summary>The instance's graceful stop command (written to the FIFO on stop); may be empty.</summary>
    public string StopCommand { get; }

    /// <summary>How long to wait for a graceful drain before <c>cgroup.kill</c> (KGSM's <c>stop_command_timeout_seconds</c>).</summary>
    public int StopTimeoutSeconds { get; }

    /// <summary>
    /// The launcher/game leader's exit code if it has exited, else null. After the launcher's
    /// <c>exec</c>, this <see cref="Process"/> <em>is</em> the game leader, so a non-zero/​signal exit
    /// (≥128) means a crash and <c>0</c> means a clean shutdown — the reconcile loop's discriminator.
    /// Reading <see cref="Process.HasExited"/> also lets .NET reap the child (no zombie).
    /// </summary>
    public int? ExitCode
    {
        get
        {
            try { return Process.HasExited ? Process.ExitCode : null; }
            catch { return null; }
        }
    }

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
