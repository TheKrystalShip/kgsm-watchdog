using TheKrystalShip.KGSM.Watchdog;
using TheKrystalShip.KGSM.Watchdog.Supervision;

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

    [Fact]
    public void RestartPolicy_defaults_to_always()
    {
        using var _ = new EnvScope(("KGSM_WATCHDOG_RESTART_POLICY", null));
        Assert.Equal(RestartPolicyMode.Always, WatchdogOptions.FromEnvironment().RestartPolicy);
    }

    [Theory]
    [InlineData("on-failure", RestartPolicyMode.OnFailure)]
    [InlineData("onfailure", RestartPolicyMode.OnFailure)]
    [InlineData("ON_FAILURE", RestartPolicyMode.OnFailure)]
    [InlineData("always", RestartPolicyMode.Always)]
    [InlineData("nonsense", RestartPolicyMode.Always)]   // unknown falls back to the default
    public void RestartPolicy_parses_leniently(string value, RestartPolicyMode expected)
    {
        using var _ = new EnvScope(("KGSM_WATCHDOG_RESTART_POLICY", value));
        Assert.Equal(expected, WatchdogOptions.FromEnvironment().RestartPolicy);
    }

    [Fact]
    public void StateFile_defaults_empty_so_the_store_derives_it_lazily()
    {
        // Empty is intentional: the real default (under the dropped user's HOME) can only be resolved
        // AFTER the bootstrap privilege drop, so DesiredStateStore derives it lazily, not here.
        using var _ = new EnvScope(("KGSM_WATCHDOG_STATE_FILE", null));
        Assert.Equal(string.Empty, WatchdogOptions.FromEnvironment().StateFile);
    }

    [Fact]
    public void StateFile_reads_explicit_override()
    {
        using var _ = new EnvScope(("KGSM_WATCHDOG_STATE_FILE", "/var/lib/kgsm-watchdog/state.json"));
        Assert.Equal("/var/lib/kgsm-watchdog/state.json", WatchdogOptions.FromEnvironment().StateFile);
    }

    [Fact]
    public void Help_documents_every_known_var()
    {
        // Completeness guard: add a knob to FromEnvironment + KnownEnvVars but forget to document it
        // in DescribeEnvironment, and this fails — config can't silently become invisible.
        var help = WatchdogOptions.DescribeEnvironment();
        foreach (var v in WatchdogOptions.KnownEnvVars)
            Assert.Contains(v, help, StringComparison.Ordinal);
        Assert.Contains("REQUIRED", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KnownEnvVars_has_no_duplicates()
        => Assert.Equal(WatchdogOptions.KnownEnvVars.Length, WatchdogOptions.KnownEnvVars.Distinct().Count());

    [Fact]
    public void UnknownConfigVars_flags_typos_not_valid_names()
    {
        using var _ = new EnvScope(
            ("KGSM_WATCHDOG_RESTART_MAX_RETRYS", "3"),    // typo (RETRYS)
            ("KGSM_WATCHDOG_RESTART_MAX_RETRIES", "3"));  // the real one

        var unknown = WatchdogOptions.UnknownConfigVars();

        Assert.Contains("KGSM_WATCHDOG_RESTART_MAX_RETRYS", unknown);
        Assert.DoesNotContain("KGSM_WATCHDOG_RESTART_MAX_RETRIES", unknown);
    }
}
