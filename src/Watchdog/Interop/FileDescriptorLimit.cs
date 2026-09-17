using System.Runtime.InteropServices;

namespace TheKrystalShip.KGSM.Watchdog.Interop;

/// <summary>
/// Raises this daemon's soft open-file limit to its hard limit, and names the soft limit its games run
/// under.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the daemon needs more than the default.</b> systemd starts a service with a soft limit of 1024.
/// Every control-plane connection, console follower, log tail, FIFO and child pipe holds a descriptor, and
/// measured on hotrod a burst of control-plane clients took all 1024: the daemon could not read a cgroup,
/// open a FIFO, spawn kgsm or write its own journal, and the servers it supervised went down with it. The
/// hard limit is the ceiling systemd grants for exactly this, and raising the soft limit to it needs no
/// privilege.
/// </para>
/// <para>
/// <b>Games keep the default.</b> The raised limit would be inherited by every spawned server, and a
/// soft limit above 1024 breaks software that still uses <c>select()</c>, whose descriptor sets cannot
/// hold a higher number. The spawn launcher sets <see cref="GameSoftLimit"/> before it execs, which is
/// the limit a game gets from any systemd service.
/// </para>
/// <para>
/// A hot-swap re-exec keeps the process's limits, so a swapped image finds the soft limit already raised
/// and this is a no-op for it.
/// </para>
/// </remarks>
internal static class FileDescriptorLimit
{
    /// <summary>The soft open-file limit every spawned game runs under: systemd's default.</summary>
    internal const int GameSoftLimit = 1024;

    /// <summary>Raises the soft limit to the hard limit.</summary>
    /// <returns>
    /// The soft limit before and after, or null when the limits could not be read or changed — in which
    /// case they are left as they were.
    /// </returns>
    internal static (ulong Before, ulong After)? RaiseSoftToHard()
    {
        var rlim = new ulong[2];
        try
        {
            if (NativeMethods.getrlimit(NativeMethods.RLIMIT_NOFILE, rlim) != 0)
                return null;

            ulong before = rlim[0];
            if (before >= rlim[1])
                return (before, before);

            rlim[0] = rlim[1];
            if (NativeMethods.setrlimit(NativeMethods.RLIMIT_NOFILE, rlim) != 0)
                return null;

            return (before, rlim[0]);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }
}
