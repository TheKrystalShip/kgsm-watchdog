using Microsoft.Extensions.Configuration;
using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Configuration binding and normalization. Most of these build a configuration in memory rather
/// than touching process-global environment variables, so they say nothing about ambient state.
/// The environment's role — overriding a settings-file key, and being flagged when misspelled —
/// is covered by the two tests that genuinely need a real environment provider.
/// </summary>
public sealed class OptionsTests
{
    private static WatchdogOptions Bind(params (string Key, string? Value)[] values)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v =>
                new KeyValuePair<string, string?>($"{WatchdogSettings.Section}:{v.Key}", v.Value)))
            .Build();

        return WatchdogOptions.FromSettings(
            config.GetSection(WatchdogSettings.Section).Get<WatchdogSettings>() ?? new WatchdogSettings());
    }

    [Fact]
    public void Defaults_when_nothing_is_configured()
    {
        var opts = Bind();

        Assert.Equal("/run/kgsm-watchdog/control.sock", opts.SocketPath);
        Assert.Equal("kgsm.slice", opts.CgroupBaseName);
        Assert.Equal("/sys/fs/cgroup/kgsm.slice", opts.CgroupBasePath);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            opts.SocketMode);
    }

    [Fact]
    public void Reads_kgsm_path_and_socket()
    {
        var opts = Bind(("KgsmPath", "/usr/local/bin/kgsm"), ("SocketPath", "/tmp/wd.sock"));

        Assert.Equal("/usr/local/bin/kgsm", opts.KgsmPath);
        Assert.Equal("/tmp/wd.sock", opts.SocketPath);
    }

    [Fact]
    public void Parses_octal_socket_mode()
    {
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
            Bind(("SocketMode", "640")).SocketMode);
    }

    [Fact]
    public void Malformed_socket_mode_keeps_the_default()
    {
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            Bind(("SocketMode", "not-octal")).SocketMode);
    }

    [Fact]
    public void Parses_controllers_list()
    {
        Assert.Equal(new[] { "cpu", "memory" }, Bind(("CgroupControllers", "cpu, memory")).CgroupControllers);
        Assert.Equal(new[] { "cpu", "memory" }, Bind(("CgroupControllers", "cpu memory")).CgroupControllers);
    }

    [Fact]
    public void RestartPolicy_defaults_to_always()
        => Assert.Equal(RestartPolicyMode.Always, Bind().RestartPolicy);

    [Theory]
    [InlineData("on-failure", RestartPolicyMode.OnFailure)]
    [InlineData("onfailure", RestartPolicyMode.OnFailure)]
    [InlineData("ON_FAILURE", RestartPolicyMode.OnFailure)]
    [InlineData("always", RestartPolicyMode.Always)]
    [InlineData("nonsense", RestartPolicyMode.Always)]   // unknown falls back to the default
    public void RestartPolicy_parses_leniently(string value, RestartPolicyMode expected)
    {
        // Kept lenient deliberately: the Control Panel declares this an enum whose wire value is
        // "on-failure", which strict enum binding would reject outright.
        Assert.Equal(expected, Bind(("RestartPolicy", value)).RestartPolicy);
    }

    [Fact]
    public void StateFile_defaults_empty_so_the_store_derives_it_lazily()
    {
        // Empty is intentional: the real default (under the dropped user's HOME) can only be resolved
        // AFTER the bootstrap privilege drop, so DesiredStateStore derives it lazily, not here.
        Assert.Equal(string.Empty, Bind().StateFile);
    }

    [Fact]
    public void StateFile_reads_explicit_override()
        => Assert.Equal("/var/lib/kgsm-watchdog/state.json",
            Bind(("StateFile", "/var/lib/kgsm-watchdog/state.json")).StateFile);

    [Fact]
    public void InstancesDir_defaults_empty_so_the_ingester_derives_it_lazily()
    {
        // Same lazy-derivation reason as StateFile: the real default (under the dropped user's HOME)
        // can only resolve after the bootstrap privilege drop, so the ingester derives it lazily.
        Assert.Equal(string.Empty, Bind().InstancesDir);
    }

    [Fact]
    public void InstancesDir_reads_explicit_override()
        => Assert.Equal("/srv/kgsm/instances", Bind(("InstancesDir", "/srv/kgsm/instances")).InstancesDir);

    [Fact]
    public void Poll_cadences_default_and_raise_sub_floor_values_to_the_floor()
    {
        Assert.Equal(1000, Bind().PlayerPresencePollMs);
        Assert.Equal(50, Bind(("PlayerPresencePollMs", "10")).PlayerPresencePollMs);
        Assert.Equal(250, Bind(("PlayerPresencePollMs", "250")).PlayerPresencePollMs);

        Assert.Equal(250, Bind().ConsolePollMs);
        Assert.Equal(50, Bind(("ConsolePollMs", "10")).ConsolePollMs);
        Assert.Equal(500, Bind(("ConsolePollMs", "500")).ConsolePollMs);

        Assert.Equal(50, Bind(("PollIntervalMs", "1")).PollIntervalMs);
        Assert.Equal(50, Bind(("ContainerLifecyclePollMs", "0")).ContainerLifecyclePollMs);
        Assert.Equal(1, Bind(("RestartStabilitySeconds", "0")).RestartStabilitySeconds);
    }

    [Fact]
    public void Blank_values_fall_back_rather_than_configuring_an_empty_path()
    {
        var opts = Bind(("SocketPath", ""), ("CgroupMountPoint", "   "), ("SupervisorLeaf", ""));

        Assert.Equal("/run/kgsm-watchdog/control.sock", opts.SocketPath);
        Assert.Equal("/sys/fs/cgroup", opts.CgroupMountPoint);
        Assert.Equal("supervisor", opts.SupervisorLeaf);
    }

    [Fact]
    public void Help_documents_every_known_var()
    {
        // Completeness guard: add a WatchdogSettings property but forget to document it in
        // DescribeEnvironment, and this fails — config cannot silently become invisible.
        var help = WatchdogOptions.DescribeEnvironment();
        foreach (var v in WatchdogOptions.KnownEnvVars)
            Assert.Contains(v, help, StringComparison.Ordinal);
        Assert.Contains("REQUIRED", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KnownEnvVars_is_derived_from_the_settings_type()
    {
        // Not a hand-maintained list: it is the settings properties, so it cannot fall behind them.
        Assert.Equal(WatchdogOptions.KnownEnvVars.Length, WatchdogOptions.KnownEnvVars.Distinct().Count());
        Assert.Equal(
            typeof(WatchdogSettings).GetProperties().Length,
            WatchdogOptions.KnownEnvVars.Length);
        Assert.Contains($"{WatchdogSettings.Section}__KgsmPath", WatchdogOptions.KnownEnvVars);
    }

    [Fact]
    public void UnknownConfigVars_flags_typos_not_valid_names()
    {
        const string Typo = "Watchdog__RestartMaxRetrys";
        const string Real = "Watchdog__RestartMaxRetries";
        Environment.SetEnvironmentVariable(Typo, "3");
        Environment.SetEnvironmentVariable(Real, "3");
        try
        {
            var unknown = WatchdogOptions.UnknownConfigVars();

            Assert.Contains(Typo, unknown);
            Assert.DoesNotContain(Real, unknown);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Typo, null);
            Environment.SetEnvironmentVariable(Real, null);
        }
    }

    [Fact]
    public void Environment_overrides_a_settings_file_key()
    {
        // The override model in one assertion: the file declares the key, the environment changes it.
        // Sources resolve in order, so the environment provider must be registered after the file.
        const string Key = "Watchdog__SocketPath";
        Environment.SetEnvironmentVariable(Key, "/tmp/from-env.sock");
        try
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>("Watchdog:SocketPath", "/from-file.sock")])
                .AddEnvironmentVariables()
                .Build();

            var opts = WatchdogOptions.FromSettings(
                config.GetSection(WatchdogSettings.Section).Get<WatchdogSettings>()!);

            Assert.Equal("/tmp/from-env.sock", opts.SocketPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Key, null);
        }
    }
}
