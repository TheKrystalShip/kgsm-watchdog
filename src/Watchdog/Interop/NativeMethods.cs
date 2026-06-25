using System.Runtime.InteropServices;

namespace TheKrystalShip.KGSM.Watchdog.Interop;

/// <summary>
/// The libc surface the watchdog needs that .NET does not expose: FIFO creation,
/// holding a raw FIFO fd open as the stdin keepalive, the self-re-exec hot-swap
/// (fcntl/execve), inode rotation detection (statx), and the cgroup-writability
/// probe (access). All entries are source-generated
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

    // fcntl(2) commands + the close-on-exec flag. Used by the self-re-exec hot-swap (Inc 7): a FIFO
    // write-fd is opened O_CLOEXEC in steady state (so a spawned game never inherits sibling fds), but
    // must shed FD_CLOEXEC for the instant of the execv so it survives into the new daemon image.
    internal const int F_GETFD = 1;
    internal const int F_SETFD = 2;
    internal const int FD_CLOEXEC = 1;

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
    /// fcntl(2) — file-descriptor control. The watchdog uses it only with the descriptor-flags commands
    /// (<see cref="F_GETFD"/>/<see cref="F_SETFD"/>) to read or clear <see cref="FD_CLOEXEC"/> on a FIFO fd
    /// around the self-re-exec swap, and as a cheap "is this fd open?" probe (<c>F_GETFD</c> returns -1 with
    /// EBADF on a closed/invalid fd). Returns -1 on error (read errno via
    /// <see cref="Marshal.GetLastPInvokeError"/>); the meaning of a non-negative result is command-specific.
    /// </summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int fcntl(int fd, int cmd, int arg);

    /// <summary><c>waitpid</c> option: return immediately (0) if no matching child has exited yet.</summary>
    internal const int WNOHANG = 1;

    /// <summary>
    /// waitpid(2) — reap a specific child. The watchdog uses it ONLY to reap a known hot-swap-ADOPTED
    /// instance (a game that survived a same-PID <c>execve</c> and so is still our child, but for which the
    /// re-exec'd image holds no managed <see cref="System.Diagnostics.Process"/>, so the runtime never reaps
    /// it). Always called with <see cref="WNOHANG"/>, so it never blocks: returns the pid when it reaps a
    /// zombie, 0 if that child is still alive, or -1 (ECHILD) if it is not / no longer our child. It targets
    /// only those specific adopted pids — NEVER <c>waitpid(-1)</c> — so it cannot race the CLR reaping the
    /// children IT spawned (the launcher, --selfcheck, kgsm-lib calls), which would corrupt their exit codes.
    /// </summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int waitpid(int pid, out int wstatus, int options);

    /// <summary>
    /// execv(3) — replace the current process image with the program at <paramref name="path"/>.
    /// <para>
    /// AOT-safe, marshalled by hand to avoid any reflection-based string/array marshalling:
    /// <paramref name="path"/> is a pointer to a NUL-terminated UTF-8 path, and <paramref name="argv"/> is a
    /// pointer to a NULL-terminated array of pointers to NUL-terminated UTF-8 argument strings (by convention
    /// <c>argv[0]</c> is the program name). The new image inherits every open fd that is not
    /// <see cref="FD_CLOEXEC"/> and the current libc <c>environ</c>.
    /// </para>
    /// <para>
    /// <b>Caveat (verified on net10, 2026-06-25):</b> <c>Environment.SetEnvironmentVariable</c> does NOT
    /// write through to libc <c>environ</c> on the CLR, so a value staged that way is NOT visible to
    /// <c>execv</c>. To carry a handoff variable across the swap, marshal an explicit <c>envp</c> and call
    /// <see cref="execve"/> instead. <c>execv</c> is kept for argv-only re-execs.
    /// </para>
    /// <para>
    /// On success execv NEVER returns — the calling image is gone. It returns only on FAILURE (-1, errno set),
    /// in which case the original image continues running intact, which is what makes a pre-exec validation
    /// gate trustworthy.
    /// </para>
    /// </summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int execv(IntPtr path, IntPtr argv);

    /// <summary>
    /// execve(2) — like <see cref="execv"/> but with an explicit, hand-marshalled environment
    /// (<paramref name="envp"/>: a NULL-terminated array of pointers to NUL-terminated UTF-8
    /// <c>KEY=VALUE</c> strings). This is the path the hot-swap uses: because
    /// <c>Environment.SetEnvironmentVariable</c> does not reach libc <c>environ</c> on net10, the only
    /// reliable way to hand the base64 handoff to the successor image is to build <c>envp</c> ourselves
    /// (the current environment + the handoff var) and pass it here. Same return contract as
    /// <see cref="execv"/>: never returns on success, returns -1 with errno on failure.
    /// </summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int execve(IntPtr path, IntPtr argv, IntPtr envp);

    /// <summary>
    /// malloc_trim(3) — return free memory from the top of the main glibc arena (and, on modern glibc,
    /// other arenas) to the OS. The periodic <see cref="Supervision.MemoryTrimmer"/> calls it AFTER an
    /// aggressive compacting <c>GC.Collect</c>: the GC hands back its own committed heap, but the native
    /// allocations behind the control plane (Kestrel/socket churn, kgsm-lib subprocess plumbing) go
    /// through glibc <c>malloc</c>, which by default keeps freed chunks cached in the arena rather than
    /// <c>munmap</c>-ing them — so they linger on the RSS until this is called. <paramref name="pad"/> is
    /// the number of bytes to leave available at the top (0 = release as much as possible). Returns 1 if
    /// memory was actually released, 0 otherwise. glibc-only (the deploy target); a no-op-or-absent libc
    /// would surface as a P/Invoke failure, so the caller guards it in a try/catch as pure optimization.
    /// </summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int malloc_trim(nuint pad);
}
