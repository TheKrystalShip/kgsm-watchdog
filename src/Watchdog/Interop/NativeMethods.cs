using System.Runtime.InteropServices;

namespace TheKrystalShip.KGSM.Watchdog.Interop;

/// <summary>
/// The libc surface the watchdog needs that .NET does not expose: FIFO creation,
/// holding a raw FIFO fd open as the stdin keepalive, and the privilege drop after
/// the root-boot cgroup bootstrap. All entries are source-generated
/// <see cref="LibraryImportAttribute"/> p/invokes (not <c>DllImport</c>) so the
/// daemon stays Native-AOT clean.
/// <para>
/// Constants are the Linux x86-64/aarch64 glibc values. <c>SetLastError = true</c>
/// lets callers read <see cref="Marshal.GetLastPInvokeError"/> on a -1 return.
/// </para>
/// </summary>
internal static partial class NativeMethods
{
    // open(2) flags (asm-generic, shared by x86-64 and aarch64).
    internal const int O_RDWR = 0x0002;
    internal const int O_CLOEXEC = 0x080000;

    // access(2) mode bits.
    internal const int W_OK = 2;

    // statx(2) — read an event-channel file's inode for rotation detection. AT_FDCWD resolves a
    // relative/absolute path from the cwd; mask STATX_INO requests only the inode; flags 0 means
    // FOLLOW symlinks (the kgsm instances dir reaches each events file THROUGH the instance→working_dir
    // symlink, so we must traverse it). statx has a fixed, architecture-independent struct layout
    // (unlike struct stat) — that is exactly why it is used here: stx_ino sits at a stable byte
    // offset on every arch, so the inode is read with no fragile struct marshalling.
    internal const int AT_FDCWD = -100;
    internal const uint STATX_INO = 0x100;

    /// <summary>Byte offset of <c>stx_ino</c> within <c>struct statx</c> (fixed across architectures).</summary>
    internal const int StatxInoOffset = 32;

    /// <summary>Size of <c>struct statx</c>; the kernel writes the whole struct, so the buffer must be full-size.</summary>
    internal const int StatxBufferSize = 256;

    /// <summary>Effective uid of the daemon — 0 means we booted as root and must self-bootstrap + drop.</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial uint geteuid();

    /// <summary>
    /// access(2) — the faithful equivalent of bash <c>[[ -w "$dir" ]]</c> used by the cgroup
    /// support probe: returns 0 when the caller may write <paramref name="pathname"/>.
    /// </summary>
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int access(string pathname, int mode);

    /// <summary>
    /// statx(2) — fetch an inode (and only an inode, via <see cref="STATX_INO"/>) for a path,
    /// following symlinks. The result is written into <paramref name="buf"/> (a full
    /// <see cref="StatxBufferSize"/>-byte <c>struct statx</c>); the caller reads <c>stx_ino</c> at
    /// <see cref="StatxInoOffset"/>. Returns 0 on success, -1 on error (read errno via
    /// <see cref="Marshal.GetLastPInvokeError"/> — e.g. ENOENT when the channel does not exist yet).
    /// </summary>
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int statx(int dirfd, string pathname, int flags, uint mask, byte[] buf);

    /// <summary>
    /// chown(2) — used during the root-boot delegation to hand the cgroup subtree and the
    /// runtime socket directory to the target user, so the daemon can manage them after it
    /// drops privilege. Pass <c>0xFFFFFFFF</c> for an id to leave it unchanged.
    /// </summary>
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int chown(string pathname, uint owner, uint group);

    /// <summary>
    /// Create a FIFO (named pipe) at <paramref name="pathname"/>. The watchdog owns the
    /// <c>mkfifo</c> the old native management script used to do, because the game's stdin
    /// must be a FIFO it can later write console/stop commands into.
    /// </summary>
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int mkfifo(string pathname, uint mode);

    /// <summary>
    /// open(2). Used to hold the FIFO write-end open from the daemon (O_RDWR so the open
    /// never blocks waiting for a peer and a write to a dead reader yields EPIPE rather than
    /// SIGPIPE-ing the daemon). O_CLOEXEC keeps this fd out of every spawned game.
    /// </summary>
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int open(string pathname, int flags, uint mode);

    /// <summary>write(2) — used to push a stop/console command into the instance FIFO.</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial nint write(int fd, byte[] buf, nuint count);

    /// <summary>close(2).</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int close(int fd);

    /// <summary>
    /// setgroups(2) — replace the supplementary group list. Called FIRST in the drop so we
    /// do not keep root's supplementary groups (a privileged context still possible after
    /// setresuid otherwise).
    /// </summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int setgroups(nuint size, uint[] list);

    /// <summary>setresgid(2) — drop real/effective/saved gid. Called BEFORE setresuid (still privileged to do so).</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int setresgid(uint rgid, uint egid, uint sgid);

    /// <summary>setresuid(2) — drop real/effective/saved uid. Called LAST; after this the daemon is unprivileged.</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int setresuid(uint ruid, uint euid, uint suid);
}
