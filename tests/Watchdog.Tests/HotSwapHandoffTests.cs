using System.Text;
using System.Text.Json;
using TheKrystalShip.KGSM.Watchdog.Interop;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// Covers the hot-swap handoff blob (Inc 7 / Option 3): it must round-trip through the source-generated
/// <see cref="WatchdogJsonContext"/> (AOT-safe, no reflection) and survive the base64(UTF8(json))
/// transport the producer and consumer agree on across the <c>execve</c>. The producer
/// (<c>PrepareAndExecHotSwap</c>) and the consumer (<c>AdoptFromHandoff</c>) are two halves of the SAME
/// contract on either side of a same-PID exec, so the encode/decode symmetry is load-bearing.
/// </summary>
[Collection(EnvironmentCollection.Name)] // BuildEnvp tests mutate process-global env vars — serialize them
public sealed class HotSwapHandoffTests
{
    [Fact]
    public void Handoff_round_trips_every_field_through_the_source_gen_context()
    {
        var spawned = new DateTime(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);
        var next = new DateTime(2026, 6, 25, 12, 1, 30, DateTimeKind.Utc);
        var original = new HotSwapHandoff
        {
            Instances =
            {
                new HotSwapEntry
                {
                    Name = "factorio-test",
                    FifoFd = 17,
                    FifoPath = "/run/factorio-test.in",
                    ConsecutiveFailures = 2,
                    GaveUp = false,
                    Phase = "Running",
                    SpawnedAt = spawned,
                    NextRestartAt = next,
                    LastReason = "re-adopted live cgroup",
                    DesiredRunning = true,
                },
                new HotSwapEntry
                {
                    Name = "7dtd",
                    FifoFd = 23,
                    FifoPath = "/run/7dtd.in",
                    ConsecutiveFailures = 5,
                    GaveUp = true,
                    Phase = "Failed",
                    SpawnedAt = null,
                    NextRestartAt = null,
                    LastReason = "gave up",
                    DesiredRunning = false,
                },
            },
        };

        string json = JsonSerializer.Serialize(original, WatchdogJsonContext.Default.HotSwapHandoff);
        var back = JsonSerializer.Deserialize(json, WatchdogJsonContext.Default.HotSwapHandoff);

        Assert.NotNull(back);
        Assert.Equal(1, back!.Version);
        Assert.Equal(2, back.Instances.Count);

        var fac = back.Instances[0];
        Assert.Equal("factorio-test", fac.Name);
        Assert.Equal(17, fac.FifoFd);
        Assert.Equal("/run/factorio-test.in", fac.FifoPath);
        Assert.Equal(2, fac.ConsecutiveFailures);
        Assert.False(fac.GaveUp);
        Assert.Equal("Running", fac.Phase);
        Assert.Equal(spawned, fac.SpawnedAt);
        Assert.Equal(next, fac.NextRestartAt);
        Assert.Equal("re-adopted live cgroup", fac.LastReason);
        Assert.True(fac.DesiredRunning);

        var sdtd = back.Instances[1];
        Assert.Equal("7dtd", sdtd.Name);
        Assert.Equal(23, sdtd.FifoFd);
        Assert.Equal(5, sdtd.ConsecutiveFailures);
        Assert.True(sdtd.GaveUp);
        Assert.Equal("Failed", sdtd.Phase);
        Assert.Null(sdtd.SpawnedAt);
        Assert.Null(sdtd.NextRestartAt);
        Assert.False(sdtd.DesiredRunning);
    }

    [Fact]
    public void Blob_survives_the_base64_utf8_transport_the_env_var_channel_uses()
    {
        var handoff = new HotSwapHandoff
        {
            Instances = { new HotSwapEntry { Name = "minecraft", FifoFd = 9, FifoPath = "/run/mc.in", Phase = "Running", DesiredRunning = true } },
        };

        // Producer side: json -> utf8 -> base64 (exactly what PrepareAndExecHotSwap stages into the env var).
        string json = JsonSerializer.Serialize(handoff, WatchdogJsonContext.Default.HotSwapHandoff);
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        // Consumer side: base64 -> utf8 -> json (exactly what AdoptFromHandoff does).
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        var back = JsonSerializer.Deserialize(decoded, WatchdogJsonContext.Default.HotSwapHandoff);

        Assert.NotNull(back);
        Assert.Single(back!.Instances);
        Assert.Equal("minecraft", back.Instances[0].Name);
        Assert.Equal(9, back.Instances[0].FifoFd);
        Assert.Equal("/run/mc.in", back.Instances[0].FifoPath);
    }

    [Fact]
    public void Empty_handoff_round_trips_to_an_empty_instance_list()
    {
        var json = JsonSerializer.Serialize(new HotSwapHandoff(), WatchdogJsonContext.Default.HotSwapHandoff);
        var back = JsonSerializer.Deserialize(json, WatchdogJsonContext.Default.HotSwapHandoff);
        Assert.NotNull(back);
        Assert.Empty(back!.Instances);
    }

    [Fact]
    public void EnvVarName_is_the_agreed_handoff_channel()
        => Assert.Equal("KGSM_WATCHDOG_HOTSWAP_HANDOFF", HotSwapHandoff.EnvVarName);

    // ---- ReExec.BuildEnvp: the explicit envp that carries the handoff across execve -------------------
    // (Environment.SetEnvironmentVariable does NOT reach libc environ on net10 — verified 2026-06-25 — so
    //  the swap marshals envp by hand. This is the pure builder behind that fallback.)

    [Fact]
    public void BuildEnvp_injects_the_override_when_it_was_not_already_present()
    {
        const string key = "KGSM_WATCHDOG_TEST_HANDOFF_ONLY_HERE_XYZ";
        string? prior = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, null); // ensure absent in this process's env
        try
        {
            var envp = ReExec.BuildEnvp(key, "the-blob");
            // Exactly one entry for our key, with the override value (appended because it wasn't inherited).
            var ours = envp.Where(e => e.StartsWith(key + "=", StringComparison.Ordinal)).ToList();
            Assert.Single(ours);
            Assert.Equal($"{key}=the-blob", ours[0]);
            // And it carried the rest of the environment (PATH is essentially always present).
            Assert.Contains(envp, e => e.StartsWith("PATH=", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(key, prior); }
    }

    [Fact]
    public void BuildEnvp_overrides_an_existing_value_exactly_once()
    {
        const string key = "KGSM_WATCHDOG_TEST_HANDOFF_PREEXISTING_XYZ";
        string? prior = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "stale-inherited-value");
        try
        {
            var envp = ReExec.BuildEnvp(key, "fresh-blob");
            var ours = envp.Where(e => e.StartsWith(key + "=", StringComparison.Ordinal)).ToList();
            Assert.Single(ours);                                   // not duplicated
            Assert.Equal($"{key}=fresh-blob", ours[0]);            // the inherited stale value is replaced
        }
        finally { Environment.SetEnvironmentVariable(key, prior); }
    }

    [Fact]
    public void BuildEnvp_with_a_null_value_removes_the_key()
    {
        const string key = "KGSM_WATCHDOG_TEST_HANDOFF_REMOVE_XYZ";
        string? prior = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, "to-be-removed");
        try
        {
            var envp = ReExec.BuildEnvp(key, null);
            Assert.DoesNotContain(envp, e => e.StartsWith(key + "=", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(key, prior); }
    }
}
