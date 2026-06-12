using TheKrystalShip.KGSM.Watchdog;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Verifies the reflection-free <see cref="WatchdogOptions.FromEnvironment"/> parsing (the
/// AOT-clean alternative to config binding). Each test fully controls the env vars it reads and
/// restores them, so the suite is order-independent.
/// </summary>
public sealed class OptionsTests
{
    /// <summary>Set a batch of env vars for the duration of the using-scope, restoring prior values.</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _prior = new();

        public EnvScope(params (string Key, string? Value)[] vars)
        {
            foreach (var (key, value) in vars)
            {
                _prior[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _prior)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    public void Defaults_when_unset()
    {
        using var _ = new EnvScope(
            ("KGSM_WATCHDOG_SOCKET", null),
            ("KGSM_WATCHDOG_SOCKET_MODE", null),
            ("KGSM_WATCHDOG_CGROUP_BASE", null),
            ("KGSM_WATCHDOG_UID", null),
            ("SUDO_UID", null));

        var opts = WatchdogOptions.FromEnvironment();

        Assert.Equal("/run/kgsm-watchdog/control.sock", opts.SocketPath);
        Assert.Equal("kgsm.slice", opts.CgroupBaseName);
        Assert.Equal("/sys/fs/cgroup/kgsm.slice", opts.CgroupBasePath);
        Assert.Null(opts.TargetUid);
    }

    [Fact]
    public void Reads_kgsm_path_and_socket()
    {
        using var _ = new EnvScope(
            ("KGSM_WATCHDOG_KGSM_PATH", "/usr/local/bin/kgsm"),
            ("KGSM_WATCHDOG_SOCKET", "/tmp/wd.sock"));

        var opts = WatchdogOptions.FromEnvironment();

        Assert.Equal("/usr/local/bin/kgsm", opts.KgsmPath);
        Assert.Equal("/tmp/wd.sock", opts.SocketPath);
    }

    [Fact]
    public void Parses_octal_socket_mode()
    {
        using var _ = new EnvScope(("KGSM_WATCHDOG_SOCKET_MODE", "640"));

        var opts = WatchdogOptions.FromEnvironment();

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            opts.SocketMode);
    }

    [Fact]
    public void Parses_controllers_list()
    {
        using var _ = new EnvScope(("KGSM_WATCHDOG_CGROUP_CONTROLLERS", "cpu, memory"));

        var opts = WatchdogOptions.FromEnvironment();

        Assert.Equal(new[] { "cpu", "memory" }, opts.CgroupControllers);
    }

    [Fact]
    public void TargetUid_explicit_overrides_sudo()
    {
        using var _ = new EnvScope(
            ("KGSM_WATCHDOG_UID", "1000"),
            ("KGSM_WATCHDOG_GID", "1000"),
            ("SUDO_UID", "0"));

        var opts = WatchdogOptions.FromEnvironment();

        Assert.Equal(1000u, opts.TargetUid);
        Assert.Equal(1000u, opts.TargetGid);
    }

    [Fact]
    public void TargetUid_falls_back_to_sudo()
    {
        using var _ = new EnvScope(
            ("KGSM_WATCHDOG_UID", null),
            ("SUDO_UID", "1001"),
            ("SUDO_GID", "1002"));

        var opts = WatchdogOptions.FromEnvironment();

        Assert.Equal(1001u, opts.TargetUid);
        Assert.Equal(1002u, opts.TargetGid);
    }
}
