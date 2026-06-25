using TheKrystalShip.KGSM.Watchdog.Supervision;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the one pure, security-relevant helper in the spawn path: the envsubst-equivalent the
/// daemon runs over <c>executable_arguments</c> before the launcher word-splits them. (The fork
/// itself needs a real instance + cgroup and is exercised live, not in the unit suite.)
/// </summary>
[Collection(EnvironmentCollection.Name)] // ExpandEnvironment tests mutate process env vars — serialize with the other env-touching classes
public sealed class SpawnEngineTests
{
    [Fact]
    public void ExpandEnvironment_passes_through_when_no_dollar()
    {
        Assert.Equal("--port 1234 --world foo", SpawnEngine.ExpandEnvironment("--port 1234 --world foo"));
    }

    [Fact]
    public void ExpandEnvironment_expands_braced_and_bare_vars()
    {
        Environment.SetEnvironmentVariable("WD_TEST_PORT", "27015");
        Environment.SetEnvironmentVariable("WD_TEST_WORLD", "alpha");
        try
        {
            Assert.Equal("-p 27015", SpawnEngine.ExpandEnvironment("-p ${WD_TEST_PORT}"));
            Assert.Equal("-w alpha end", SpawnEngine.ExpandEnvironment("-w $WD_TEST_WORLD end"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WD_TEST_PORT", null);
            Environment.SetEnvironmentVariable("WD_TEST_WORLD", null);
        }
    }

    [Fact]
    public void ExpandEnvironment_unknown_var_becomes_empty()
    {
        Environment.SetEnvironmentVariable("WD_TEST_ABSENT", null);
        Assert.Equal("a  b", SpawnEngine.ExpandEnvironment("a ${WD_TEST_ABSENT} b"));
    }

    [Fact]
    public void ExpandEnvironment_trailing_dollar_is_literal()
    {
        Assert.Equal("cost$", SpawnEngine.ExpandEnvironment("cost$"));
    }

    [Fact]
    public void ExpandEnvironment_resolves_instance_vars_like_envsubst()
    {
        // The real 7dtd case: args reference $instance_install_dir, which KGSM exports in the bash
        // path. The resolver must map it from the Instance, not blank it from the daemon's env.
        var inst = new TheKrystalShip.KGSM.Core.Models.Instance { InstallDir = "/opt/7dtd/install" };
        var resolve = SpawnEngine.BuildVarResolver(inst);

        Assert.Equal(
            "-dedicated -configfile=/opt/7dtd/install/serverconfig.xml",
            SpawnEngine.ExpandEnvironment("-dedicated -configfile=$instance_install_dir/serverconfig.xml", resolve));
    }

    [Fact]
    public void ValidateSpawnable_empty_descriptor_reports_no_usable_info_not_a_blank_field()
    {
        // The bug: an unpopulated descriptor (no name) used to fall through to the per-field check and
        // throw "{name}: executable_file is empty" with a BLANK name → the crash alert read
        // "start failed: : executable_file is empty" (a missing field). It must now name the real cause
        // and never produce the empty interpolated field.
        string? msg = SpawnEngine.ValidateSpawnable(new TheKrystalShip.KGSM.Core.Models.Instance());

        Assert.Equal("instance descriptor is empty (kgsm returned no usable info)", msg);
        Assert.DoesNotContain(": :", $"start failed: {msg}"); // the doubled colon is gone
    }

    [Fact]
    public void ValidateSpawnable_named_but_missing_exe_keeps_the_name()
    {
        // A real, resolved instance with a genuinely empty executable_file still names itself — the
        // per-field message is honest because the name is present.
        string? msg = SpawnEngine.ValidateSpawnable(
            new TheKrystalShip.KGSM.Core.Models.Instance { Name = "factorio-test" });

        Assert.Equal("factorio-test: executable_file is empty", msg);
    }

    [Fact]
    public void ValidateSpawnable_fully_populated_descriptor_is_null()
    {
        var inst = new TheKrystalShip.KGSM.Core.Models.Instance
        {
            Name = "factorio-test",
            ExecutableFile = "./factorio",
            SocketFile = "/run/factorio-test.in",
            LogFile = "/var/log/factorio-test.log",
            InstallDir = "/opt/factorio-test",
        };

        Assert.Null(SpawnEngine.ValidateSpawnable(inst));
    }
}
