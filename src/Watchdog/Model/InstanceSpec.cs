using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Engine.Contracts.Blueprints;
using TheKrystalShip.KGSM.Engine.Contracts.Instances;
using TheKrystalShip.KGSM.Engine.Contracts.Network;
using TheKrystalShip.KGSM.Engine.Instances;
using TheKrystalShip.KGSM.Engine.Network;
using TheKrystalShip.KGSM.Engine.Placeholders;

namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// What the daemon needs to know about one instance to supervise it: the spawn configuration, the
/// paths, the player-presence patterns and the per-instance switches. It is read from the engine's
/// instance config and derived layout (<see cref="From"/>), never assembled from anything else, and it
/// carries the launch arguments already resolved by the engine core, so the daemon expands nothing.
/// </summary>
public sealed record InstanceSpec
{
    public string Name { get; set; } = string.Empty;

    public string Blueprint { get; set; } = string.Empty;

    public BlueprintRuntime? Runtime { get; set; }

    public string WorkingDir { get; set; } = string.Empty;

    public string InstallDir { get; set; } = string.Empty;

    public string LaunchDir { get; set; } = string.Empty;

    public string LogsDir { get; set; } = string.Empty;

    public string SavesDir { get; set; } = string.Empty;

    public string TempDir { get; set; } = string.Empty;

    public string BackupsDir { get; set; } = string.Empty;

    public string LogFile { get; set; } = string.Empty;

    /// <summary>The instance's console input pipe.</summary>
    public string SocketFile { get; set; } = string.Empty;

    public string ExecutableFile { get; set; } = string.Empty;

    public string ExecutableSubdirectory { get; set; } = string.Empty;

    /// <summary>The launch arguments with every placeholder resolved, one entry per argument.</summary>
    public IReadOnlyList<string> LaunchArguments { get; set; } = [];

    public string LevelName { get; set; } = string.Empty;

    public string? CpuPriority { get; set; }

    public int? MemoryCapMb { get; set; }

    public bool? CrashRestart { get; set; }

    public int? CrashMaxRestarts { get; set; }

    public string StopCommand { get; set; } = string.Empty;

    public int StopCommandTimeoutSeconds { get; set; } = 30;

    public string StartupSuccessRegex { get; set; } = string.Empty;

    public string PlayerJoinedRegex { get; set; } = string.Empty;

    public string PlayerLeftRegex { get; set; } = string.Empty;

    public int? RconPort { get; set; }

    public string RconPassword { get; set; } = string.Empty;

    public int? RconPollIntervalSeconds { get; set; }

    public string RconPlayersCommand { get; set; } = "players";

    public string RconPlayersRegex { get; set; } = string.Empty;

    public bool EnablePortForwarding { get; set; }

    public bool EnableFirewallManagement { get; set; }

    public IReadOnlyList<PortMapping> Ports { get; set; } = [];

    /// <summary>Why the launch arguments could not be resolved (an unknown placeholder, an unmatched brace); null when they could.</summary>
    public string? LaunchArgumentsProblem { get; set; }

    /// <summary>
    /// The spec of an instance the engine describes, or null when its config cannot be read: an instance
    /// without a config has nothing to supervise.
    /// </summary>
    public static InstanceSpec? From(InstanceDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (description.Config is not { } config) return null;

        InstanceLayout layout = description.Layout;
        IReadOnlyList<string> arguments = [];
        string? problem = null;
        try
        {
            arguments = InstancePlaceholders.ResolveLaunchArguments(config, layout);
        }
        catch (PlaceholderException ex)
        {
            problem = ex.Message;
        }

        return new InstanceSpec
        {
            Name = description.Id,
            Blueprint = description.Blueprint,
            Runtime = config.Runtime,
            WorkingDir = layout.WorkingDirectory,
            InstallDir = layout.InstallDirectory,
            LaunchDir = layout.LaunchDirectory,
            LogsDir = layout.LogsDirectory,
            SavesDir = layout.SavesDirectory,
            TempDir = layout.TempDirectory,
            BackupsDir = config.BackupsDirectory,
            LogFile = layout.LogFile,
            SocketFile = layout.InputFifo,
            ExecutableFile = config.ExecutableFile,
            ExecutableSubdirectory = config.ExecutableSubdirectory,
            LaunchArguments = arguments,
            LaunchArgumentsProblem = problem,
            LevelName = config.LevelName,
            CpuPriority = config.CpuPriority,
            MemoryCapMb = config.MemoryCapMb,
            CrashRestart = config.CrashRestart,
            CrashMaxRestarts = config.CrashMaxRestarts,
            StopCommand = config.StopCommand,
            StopCommandTimeoutSeconds = config.StopCommandTimeoutSeconds,
            StartupSuccessRegex = config.StartupSuccessRegex,
            PlayerJoinedRegex = config.PlayerJoinedRegex,
            PlayerLeftRegex = config.PlayerLeftRegex,
            RconPort = config.RconPort,
            RconPassword = config.RconPassword,
            RconPollIntervalSeconds = config.RconPollIntervalSeconds,
            RconPlayersCommand = config.RconPlayersCommand,
            RconPlayersRegex = config.RconPlayersRegex,
            EnablePortForwarding = config.EnablePortForwarding,
            EnableFirewallManagement = config.EnableFirewallManagement,
            Ports =
            [
                .. PortSpecs.Parse(config.Ports).Select(range => new PortMapping
                {
                    Start = range.Start,
                    End = range.End,
                    Protocol = range.Protocol == PortProtocol.Udp ? "udp" : "tcp",
                }),
            ],
        };
    }
}
