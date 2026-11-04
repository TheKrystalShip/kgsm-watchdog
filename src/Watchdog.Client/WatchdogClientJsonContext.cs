using System.Text.Json.Serialization;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Client;

/// <summary>The control surface's wire types, camelCase as the daemon writes them.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ActionResult))]
[JsonSerializable(typeof(InstanceState))]
[JsonSerializable(typeof(InstanceState[]))]
[JsonSerializable(typeof(InstanceRunTimes[]))]
[JsonSerializable(typeof(ReadyState))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(WatchdogVersionInfo))]
internal sealed partial class WatchdogClientJsonContext : JsonSerializerContext;
