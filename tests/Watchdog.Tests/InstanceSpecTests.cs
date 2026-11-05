using System.Text.Json;
using TheKrystalShip.KGSM.Engine.Contracts;
using TheKrystalShip.KGSM.Engine.Contracts.Blueprints;
using TheKrystalShip.KGSM.Engine.Contracts.Instances;
using TheKrystalShip.KGSM.Engine.Contracts.Libraries;
using TheKrystalShip.KGSM.Engine.Instances;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// The spec the daemon supervises is built from the engine's instance config and derived layout, and the
/// launch arguments come out of the engine core already resolved.
/// </summary>
public sealed class InstanceSpecTests
{
    private static InstanceDescription Describe(string arguments, string ports = "", string executable = "factorio")
    {
        string json = $$"""
            {
              "schemaVersion": 1, "name": "factorio-1", "displayName": "", "blueprint": "factorio", "runtime": "native",
              "installNonce": "n", "backupsDirectory": "/b", "platform": "linux", "autoUpdateBeforeStart": false,
              "cpuPriority": "low", "memoryCapMb": 2048, "maintenanceWindows": "", "timezone": "",
              "backupRetention": 3, "announceLeadMinutes": "", "announceMaintenanceMessage": "",
              "announceMaintenanceCancelledMessage": "", "crashRestart": true, "crashMaxRestarts": 4,
              "startupSuccessRegex": "ready", "playerJoinedRegex": "joined (?<name>.+)", "playerLeftRegex": "",
              "rconPassword": "", "rconPollIntervalSeconds": 0, "rconPlayersCommand": "", "rconPlayersRegex": "",
              "levelName": "my world", "executableFile": "{{executable}}", "executableSubdirectory": "bin",
              "executableArguments": "{{arguments}}", "steamAppId": 0, "clientSteamAppId": 0, "steamcmdArguments": "",
              "isSteamAccountRequired": false, "ports": "{{ports}}", "enableFirewallManagement": true,
              "enablePortForwarding": true, "downloadTimeoutSeconds": 0, "stopCommand": "/quit",
              "saveCommand": "", "saveCommandTimeoutSeconds": 0, "stopCommandTimeoutSeconds": 15, "kickCommand": "",
              "banCommand": "", "unbanCommand": "", "broadcastCommand": "", "compressBackups": false
            }
            """;

        return new InstanceDescription
        {
            Id = "factorio-1",
            Blueprint = "factorio",
            WorkingDirectory = "/lib/instances/factorio/factorio-1",
            LibraryState = InstanceLibraryState.Online,
            Config = JsonSerializer.Deserialize(json, EngineJsonContext.Default.InstanceConfig),
        };
    }

    [Fact]
    public void The_spec_takes_its_paths_from_the_derived_layout_and_its_settings_from_the_config()
    {
        InstanceSpec spec = InstanceSpec.From(Describe("--start-server"))!;

        Assert.Equal("factorio-1", spec.Name);
        Assert.Equal(BlueprintRuntime.Native, spec.Runtime);
        Assert.Equal("/lib/instances/factorio/factorio-1/install/bin", spec.LaunchDir);
        Assert.Equal("/lib/instances/factorio/factorio-1/factorio-1.log", spec.LogFile);
        Assert.Equal("/lib/instances/factorio/factorio-1/.factorio-1.sock", spec.SocketFile);
        Assert.Equal(2048, spec.MemoryCapMb);
        Assert.Equal("low", spec.CpuPriority);
        Assert.Equal(4, spec.CrashMaxRestarts);
        Assert.Equal(15, spec.StopCommandTimeoutSeconds);
        Assert.True(spec.EnableFirewallManagement);
    }

    [Fact]
    public void Launch_arguments_arrive_resolved_one_entry_per_argument()
    {
        InstanceSpec spec = InstanceSpec.From(Describe("--world {levelName} --dir {installDir}/data"))!;

        Assert.Equal(
            ["--world", "my world", "--dir", "/lib/instances/factorio/factorio-1/install/data"],
            spec.LaunchArguments);
        Assert.Null(spec.LaunchArgumentsProblem);
    }

    [Fact]
    public void An_argument_the_engine_cannot_resolve_is_a_problem_the_spawn_reports()
    {
        InstanceSpec spec = InstanceSpec.From(Describe("--x {noSuchKey}"))!;

        Assert.Empty(spec.LaunchArguments);
        Assert.NotNull(spec.LaunchArgumentsProblem);
        Assert.Contains("launch arguments", SpawnEngine_ValidateMessage(spec));
    }

    [Fact]
    public void Declared_ports_become_one_mapping_per_range_and_protocol()
    {
        InstanceSpec spec = InstanceSpec.From(Describe("", ports: "34197/udp|27015:27016/tcp"))!;

        Assert.Contains(spec.Ports, p => p is { Start: 34197, End: 34197, Protocol: "udp" });
        Assert.Contains(spec.Ports, p => p is { Start: 27015, End: 27016, Protocol: "tcp" });
    }

    [Fact]
    public void An_instance_whose_config_cannot_be_read_has_no_spec()
    {
        InstanceDescription broken = Describe("") with { Config = null, ConfigProblem = "unreadable" };

        Assert.Null(InstanceSpec.From(broken));
    }

    private static string SpawnEngine_ValidateMessage(InstanceSpec spec) =>
        Supervision.SpawnEngine.ValidateSpawnable(spec with { SocketFile = "/s", LogFile = "/l", InstallDir = "/i" }) ?? string.Empty;
}
