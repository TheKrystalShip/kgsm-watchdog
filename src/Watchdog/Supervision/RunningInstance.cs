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
/// signal. <b>Null for an ADOPTED handle</b> (<see cref="Adopt"/>): a game re-attached after a daemon
/// bounce is no longer the daemon's child, so there is no <see cref="Process"/> and no exit code — the
/// reconcile loop supervises it by cgroup liveness, and the PID is recovered from <c>cgroup.procs</c>;</item>
/// <item>the FIFO keepalive <see cref="_fifoFd"/> — an O_RDWR fd the daemon holds open so the game's
/// stdin never hits EOF, and the channel it writes stop/console commands into. An adopted handle
/// re-opens the SURVIVING FIFO (the daemon no longer deletes it on shutdown — see
/// <see cref="ReleaseKeepingFifo"/>) so console + graceful stop are restored without a respawn.</item>
/// </list>
/// Desired-state and the restart counter do <em>not</em> live here — they must survive this handle's
/// disposal across a respawn, so they live on the durable <see cref="SupervisedInstance"/>.
/// </summary>
internal sealed class RunningInstance : IDisposable
{
    private readonly int _fifoFd;
    private readonly string _fifoPath;
    private readonly int? _adoptedPid;
    private readonly ILogger _log;
    private bool _disposed;

    private RunningInstance(
        string name,
        Process? process,
        int fifoFd,
        string fifoPath,
        string stopCommand,
        int stopTimeoutSeconds,
        int? adoptedPid,
        ILogger log)
    {
        Name = name;
        Process = process;
        _fifoFd = fifoFd;
        _fifoPath = fifoPath;
        _adoptedPid = adoptedPid;
        StopCommand = stopCommand;
        StopTimeoutSeconds = stopTimeoutSeconds;
        _log = log;
    }

    /// <summary>A freshly-spawned handle: the daemon is the game's parent and owns its FIFO.</summary>
    public RunningInstance(
        string name,
        Process process,
        int fifoFd,
        string fifoPath,
        string stopCommand,
        int stopTimeoutSeconds,
        ILogger log)
        : this(name, process, fifoFd, fifoPath, stopCommand, stopTimeoutSeconds, adoptedPid: null, log)
    {
    }

    /// <summary>
    /// An ADOPTED handle: re-attach to a game that outlived a daemon bounce. There is no child
    /// <see cref="Process"/> (the daemon is not its parent), so <see cref="ExitCode"/> is always null and
    /// liveness comes from the cgroup; <paramref name="pid"/> is recovered from <c>cgroup.procs</c> for
    /// display. <paramref name="fifoFd"/> is the re-opened (surviving) stdin FIFO, so <see cref="SendLine"/>
    /// — console and the graceful stop command — works exactly as for a spawned handle.
    /// </summary>
    public static RunningInstance Adopt(
        string name, int fifoFd, string fifoPath, string stopCommand, int stopTimeoutSeconds, int? pid, ILogger log)
        => new(name, process: null, fifoFd, fifoPath, stopCommand, stopTimeoutSeconds, adoptedPid: pid, log);

    public string Name { get; }
    public Process? Process { get; }

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
            if (Process is null) return null; // adopted: non-child, no waitpid — liveness is the cgroup's job
            try { return Process.HasExited ? Process.ExitCode : null; }
            catch { return null; }
        }
    }

    /// <summary>The leader PID: from the child <see cref="Process"/> when spawned, or the cgroup-recovered PID for an adopted handle.</summary>
    public int? Pid
    {
        get
        {
            if (Process is null) return _adoptedPid; // adopted: recovered from cgroup.procs
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

    /// <summary>
    /// Full teardown of a STOPPED/CRASHED instance: close our fd AND remove the FIFO node. Use this when
    /// the instance is going away (graceful stop, failed spawn, respawn) — the game is gone, so the FIFO
    /// is dead and should be cleaned up.
    /// </summary>
    public void Dispose() => Close(deleteFifo: true);

    /// <summary>
    /// Release the daemon's handle WITHOUT destroying the instance's stdin FIFO — used on daemon shutdown,
    /// where the game keeps running. Deleting the FIFO here is what orphaned a live game's stdin (it kept
    /// an unlinked inode no successor daemon could re-open). Closing only our fd is harmless — the kernel
    /// keeps the pipe alive while the game holds the read end — so the next daemon can re-open the
    /// surviving node on adoption and restore console + graceful stop.
    /// </summary>
    public void ReleaseKeepingFifo() => Close(deleteFifo: false);

    private void Close(bool deleteFifo)
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_fifoFd >= 0)
            NativeMethods.close(_fifoFd);

        if (deleteFifo)
        {
            try { if (File.Exists(_fifoPath)) File.Delete(_fifoPath); }
            catch (Exception ex) { _log.LogDebug(ex, "could not remove FIFO {Path}", _fifoPath); }
        }

        try { Process?.Dispose(); }
        catch { /* already gone */ }
    }
}
