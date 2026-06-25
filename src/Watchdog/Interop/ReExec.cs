using System.Runtime.InteropServices;

namespace TheKrystalShip.KGSM.Watchdog.Interop;

/// <summary>
/// The self-re-exec ("hot-swap") primitives the watchdog uses to replace its own binary in place
/// (Inc 7 / Option 3): same PID, so every supervised game stays a child and each open FIFO write-fd
/// survives the swap continuously — no stdin EOF, the one thing a process-restart cannot give.
/// <para>
/// All entries are tiny, side-effect-light wrappers over <see cref="NativeMethods"/> so they stay
/// Native-AOT clean: no reflection, no <c>unsafe</c> blocks of our own (only <see cref="IntPtr"/> +
/// <see cref="Marshal"/>). Nothing here is wired into the daemon yet — a later phase calls
/// <see cref="Exec"/> from the swap coordinator.
/// </para>
/// </summary>
internal static class ReExec
{
    /// <summary>
    /// Clear <c>FD_CLOEXEC</c> on <paramref name="fd"/> so it survives an <c>execv</c> into the new
    /// daemon image (steady-state FIFO fds carry the flag; we shed it only for the instant of the swap).
    /// Returns false if either fcntl call fails.
    /// </summary>
    internal static bool ClearCloexec(int fd)
    {
        int f = NativeMethods.fcntl(fd, NativeMethods.F_GETFD, 0);
        if (f < 0) return false;
        return NativeMethods.fcntl(fd, NativeMethods.F_SETFD, f & ~NativeMethods.FD_CLOEXEC) == 0;
    }

    /// <summary>
    /// Set <c>FD_CLOEXEC</c> on <paramref name="fd"/> — restores the steady-state invariant after a swap
    /// (or after an aborted swap whose <c>execv</c> returned), so a future spawned game never inherits it.
    /// Returns false if either fcntl call fails.
    /// </summary>
    internal static bool SetCloexec(int fd)
    {
        int f = NativeMethods.fcntl(fd, NativeMethods.F_GETFD, 0);
        if (f < 0) return false;
        return NativeMethods.fcntl(fd, NativeMethods.F_SETFD, f | NativeMethods.FD_CLOEXEC) == 0;
    }

    /// <summary>
    /// True when <paramref name="fd"/> is a non-negative, currently-open descriptor. Used after a re-exec
    /// to confirm an inherited FIFO fd actually carried across before adopting it (a closed/invalid fd makes
    /// <c>fcntl(fd, F_GETFD)</c> fail with EBADF).
    /// </summary>
    internal static bool IsValidFd(int fd)
        => fd >= 0 && NativeMethods.fcntl(fd, NativeMethods.F_GETFD, 0) >= 0;

    /// <summary>
    /// The resolved path of the running binary, i.e. the re-exec target. Native-AOT single-file runs in
    /// place (not extracted to a temp dir), so <see cref="Environment.ProcessPath"/> is the real install
    /// path — exactly the file a deploy overwrites and we should exec.
    /// </summary>
    internal static string? SelfPath => Environment.ProcessPath;

    /// <summary>
    /// Replace the current process image with <paramref name="path"/>, passing <paramref name="argv"/> as the
    /// new program's argument vector (by convention <c>argv[0]</c> is the program name). The new image inherits
    /// the current environment and every non-<c>FD_CLOEXEC</c> fd.
    /// <para>
    /// On success this method NEVER returns — the calling image is gone. It returns ONLY on failure: the libc
    /// errno from the failed <c>execv</c> (the original image continues running intact). All unmanaged
    /// allocations are freed before returning so a failed swap does not leak.
    /// </para>
    /// </summary>
    /// <returns>The errno from the failed <c>execv</c> (never returns on success).</returns>
    internal static int Exec(string path, IReadOnlyList<string> argv)
    {
        IntPtr pathPtr = IntPtr.Zero;
        IntPtr argvPtr = IntPtr.Zero;
        // Track each arg allocation separately: the argv array holds the same pointers, but we free our own
        // list to be explicit and independent of how many slots we wrote before any early failure.
        var argPtrs = new IntPtr[argv.Count];
        try
        {
            pathPtr = Marshal.StringToCoTaskMemUTF8(path);

            // (count + 1) entries: one pointer per arg, plus a trailing NULL terminator execv requires.
            argvPtr = Marshal.AllocCoTaskMem((argv.Count + 1) * IntPtr.Size);
            for (int i = 0; i < argv.Count; i++)
            {
                argPtrs[i] = Marshal.StringToCoTaskMemUTF8(argv[i]);
                Marshal.WriteIntPtr(argvPtr, i * IntPtr.Size, argPtrs[i]);
            }
            Marshal.WriteIntPtr(argvPtr, argv.Count * IntPtr.Size, IntPtr.Zero);

            // Returns only on failure; on success the image is replaced and control never comes back here.
            NativeMethods.execv(pathPtr, argvPtr);
            return Marshal.GetLastPInvokeError();
        }
        finally
        {
            // Reached only when execv failed (or threw before the call) — clean up every allocation.
            for (int i = 0; i < argPtrs.Length; i++)
            {
                if (argPtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(argPtrs[i]);
            }
            if (argvPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(argvPtr);
            if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
        }
    }
}
