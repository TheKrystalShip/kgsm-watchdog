using TheKrystalShip.KGSM.Watchdog.Interop;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the cloexec/fd helpers that the self-re-exec hot-swap (Inc 7 / Option 3) relies on, against a
/// REAL open file descriptor — no mocking of fcntl. Deterministic and process-safe: it NEVER calls
/// <see cref="ReExec.Exec"/> (that would replace the test runner's image) and never forks. The fd is the
/// raw descriptor behind a temp <see cref="FileStream"/>, so SetCloexec/ClearCloexec actually flip the
/// kernel's FD_CLOEXEC bit and we read it back via fcntl(F_GETFD).
/// </summary>
public sealed class ReExecTests
{
    [Fact]
    public void Cloexec_set_and_clear_flip_the_real_fd_flag_and_invalid_fds_are_rejected()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"wd-reexec-{Guid.NewGuid():N}");
        using var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite);
        try
        {
            int fd = fs.SafeFileHandle.DangerousGetHandle().ToInt32();

            // A live, open fd is valid.
            Assert.True(ReExec.IsValidFd(fd), "an open file descriptor must report valid");

            // Set close-on-exec, then confirm the kernel actually carries the bit.
            Assert.True(ReExec.SetCloexec(fd), "SetCloexec should succeed on a live fd");
            int afterSet = NativeMethods.fcntl(fd, NativeMethods.F_GETFD, 0);
            Assert.True(afterSet >= 0, "F_GETFD should succeed on a live fd");
            Assert.True((afterSet & NativeMethods.FD_CLOEXEC) != 0, "FD_CLOEXEC must be set after SetCloexec");

            // Clear it — this is what the swap does just before execv so the fd survives into the new image.
            Assert.True(ReExec.ClearCloexec(fd), "ClearCloexec should succeed on a live fd");
            int afterClear = NativeMethods.fcntl(fd, NativeMethods.F_GETFD, 0);
            Assert.True(afterClear >= 0, "F_GETFD should succeed on a live fd");
            Assert.True((afterClear & NativeMethods.FD_CLOEXEC) == 0, "FD_CLOEXEC must be clear after ClearCloexec");

            // -1 is never a valid fd (the after-re-exec inherited-fd check must reject it without a syscall trap).
            Assert.False(ReExec.IsValidFd(-1), "-1 must never report valid");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
