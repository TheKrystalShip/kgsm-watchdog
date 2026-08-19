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
[JsonSerializable(typeof(PersistedRunHistory))] // companion run-history.json (how each run ended — the crash↔console join)
[JsonSerializable(typeof(PersistedPlayerNames))] // companion player-names.json (the account id → display name each instance reported)
[JsonSerializable(typeof(HotSwapHandoff))] // base64'd env-var handoff carried across a self-re-exec hot-swap (Inc 7 / Option 3)
[JsonSerializable(typeof(string[]))] // GET /enabled — the boot-autostart name set
[JsonSerializable(typeof(PlayerPresenceLine))] // one NDJSON line of a container's player-presence channel
[JsonSerializable(typeof(ContainerLifecycleLine))] // one NDJSON line of a container's lifecycle channel (UPnP + run-state)
[JsonSerializable(typeof(WatchdogVersionInfo))] // GET /version — build identity for the hot-swap deploy
[JsonSerializable(typeof(PlayerSession))] // GET /players — one player session in the live session map
[JsonSerializable(typeof(PlayerSession[]))] // GET /players — array of sessions per instance
[JsonSerializable(typeof(InstancePresence))] // GET /players — one instance's detection + its sessions
[JsonSerializable(typeof(Dictionary<string, InstancePresence>))] // GET /players — every instance, keyed by name
[JsonSerializable(typeof(UpnpMapping))] // GET /upnp/{name} — one IGD redirection row
[JsonSerializable(typeof(UpnpListResult))] // GET /upnp/{name} — the instance's IGD mappings + honest state
[JsonSerializable(typeof(ConsoleRun))] // GET /console/{name}/runs — one run of an instance's console
[JsonSerializable(typeof(ConsoleRun[]))] // GET /console/{name}/runs — the run list, newest first
internal sealed partial class WatchdogJsonContext : JsonSerializerContext;
