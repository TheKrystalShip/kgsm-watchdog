using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// System.Text.Json source-generation context for the control surface DTOs. Keeps the daemon
/// reflection-free so it stays Native-AOT/trim-clean (no IL2026/IL3050). camelCase on the wire
/// for the clients (kgsm-lib's future typed client, the bot, the CLI).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ActionResult))]
[JsonSerializable(typeof(InstanceState))]
[JsonSerializable(typeof(InstanceState[]))]
[JsonSerializable(typeof(ReadyState))]
[JsonSerializable(typeof(PersistedDesiredState))]
[JsonSerializable(typeof(PersistedSupervisionState))] // companion supervision-state.json (restart counters survive any daemon death)
[JsonSerializable(typeof(HotSwapHandoff))] // base64'd env-var handoff carried across a self-re-exec hot-swap (Inc 7 / Option 3)
[JsonSerializable(typeof(string[]))] // GET /enabled — the boot-autostart name set
[JsonSerializable(typeof(PlayerPresenceLine))] // one NDJSON line of a container's player-presence channel
[JsonSerializable(typeof(WatchdogVersionInfo))] // GET /version — build identity for the hot-swap deploy
internal sealed partial class WatchdogJsonContext : JsonSerializerContext;
