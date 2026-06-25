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
        var argPtrs = new IntPtr[argv.Count];
        try
        {
            pathPtr = Marshal.StringToCoTaskMemUTF8(path);
            argvPtr = BuildStringArray(argv, argPtrs);

            // Returns only on failure; on success the image is replaced and control never comes back here.
            NativeMethods.execv(pathPtr, argvPtr);
            return Marshal.GetLastPInvokeError();
        }
        finally
        {
            FreeStringArray(argPtrs, argvPtr);
            if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>
    /// Like <see cref="Exec"/> but with an EXPLICIT environment (<c>execve</c>). This is the path the
    /// hot-swap takes, because <c>Environment.SetEnvironmentVariable</c> does NOT reach libc <c>environ</c>
    /// on net10 (verified 2026-06-25), so a handoff staged that way would be invisible to a plain
    /// <c>execv</c>. The successor therefore receives <paramref name="env"/> verbatim — build it with
    /// <see cref="BuildEnvp"/> (the current process environment + the handoff override) so it inherits the
    /// daemon's full config plus the handoff. Same return contract: never returns on success, returns the
    /// errno on failure; all unmanaged allocations are freed before returning.
    /// </summary>
    /// <returns>The errno from the failed <c>execve</c> (never returns on success).</returns>
    internal static int ExecWithEnv(string path, IReadOnlyList<string> argv, IReadOnlyList<string> env)
    {
        IntPtr pathPtr = IntPtr.Zero;
        IntPtr argvPtr = IntPtr.Zero;
        IntPtr envpPtr = IntPtr.Zero;
        var argPtrs = new IntPtr[argv.Count];
        var envPtrs = new IntPtr[env.Count];
        try
        {
            pathPtr = Marshal.StringToCoTaskMemUTF8(path);
            argvPtr = BuildStringArray(argv, argPtrs);
            envpPtr = BuildStringArray(env, envPtrs);

            NativeMethods.execve(pathPtr, argvPtr, envpPtr);
            return Marshal.GetLastPInvokeError();
        }
        finally
        {
            FreeStringArray(argPtrs, argvPtr);
            FreeStringArray(envPtrs, envpPtr);
            if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    /// <summary>
    /// Build the <c>envp</c> string list (<c>KEY=VALUE</c> entries) for an <c>execve</c>: a snapshot of the
    /// CURRENT process environment, with one <paramref name="overrideKey"/> set to
    /// <paramref name="overrideValue"/> (or removed when the value is null). Pure (no syscalls, no exec) so
    /// it is unit-testable in isolation — the marshalling-correctness check the Phase 6 live exec cannot
    /// give cheaply. Reads the environment via <see cref="Environment.GetEnvironmentVariables"/> (the CLR
    /// view), which is fine because the successor will run on .NET and read it back through the same view —
    /// the only thing libc-environ visibility actually blocked was the handoff staged AFTER process start.
    /// </summary>
    internal static List<string> BuildEnvp(string overrideKey, string? overrideValue)
    {
        var env = new List<string>();
        bool wrote = false;
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is not string key)
                continue;
            if (string.Equals(key, overrideKey, StringComparison.Ordinal))
            {
                if (overrideValue is not null) { env.Add($"{key}={overrideValue}"); }
                wrote = true; // skip the inherited value either way (override or removal)
                continue;
            }
            env.Add($"{key}={e.Value}");
        }
        if (!wrote && overrideValue is not null)
            env.Add($"{overrideKey}={overrideValue}");
        return env;
    }

    /// <summary>
    /// Marshal <paramref name="items"/> into a freshly-allocated, NULL-terminated <c>char**</c> of UTF-8
    /// strings; the per-item pointers are also recorded in <paramref name="itemPtrs"/> so the caller can
    /// free them. Returns the array pointer. AOT-safe (no reflection marshalling).
    /// </summary>
    private static IntPtr BuildStringArray(IReadOnlyList<string> items, IntPtr[] itemPtrs)
    {
        // (count + 1) entries: one pointer per item, plus the trailing NULL terminator execv/execve require.
        IntPtr arr = Marshal.AllocCoTaskMem((items.Count + 1) * IntPtr.Size);
        for (int i = 0; i < items.Count; i++)
        {
            itemPtrs[i] = Marshal.StringToCoTaskMemUTF8(items[i]);
            Marshal.WriteIntPtr(arr, i * IntPtr.Size, itemPtrs[i]);
        }
        Marshal.WriteIntPtr(arr, items.Count * IntPtr.Size, IntPtr.Zero);
        return arr;
    }

    private static void FreeStringArray(IntPtr[] itemPtrs, IntPtr arr)
    {
        for (int i = 0; i < itemPtrs.Length; i++)
        {
            if (itemPtrs[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(itemPtrs[i]);
        }
        if (arr != IntPtr.Zero) Marshal.FreeCoTaskMem(arr);
    }
}
