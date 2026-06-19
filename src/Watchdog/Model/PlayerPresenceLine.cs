using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// One line of the in-container → host player-presence event channel (the NDJSON schema the
/// kgsm-containers detection shim writes, one JSON object per line). The watchdog tails this and
/// re-emits each line as a kgsm wire event. Schema (frozen contract, player-presence Increment 1):
/// <code>
/// {"type":"player_joined","id":"&lt;str|null&gt;","name":"&lt;str|null&gt;","ts":"&lt;ISO-8601-UTC&gt;"}
/// {"type":"player_left","id":"&lt;str|null&gt;","name":"&lt;str|null&gt;","ts":"&lt;ISO-8601-UTC&gt;"}
/// </code>
/// <para>
/// <see cref="Id"/> is an opaque, game-scoped stable id (SteamID64 / MC UUID) when the source gives
/// one; <see cref="Name"/> is the display label. Either may be <see langword="null"/> — a source that
/// surfaces only one leaves the other null, and the missing one is <b>never fabricated</b>. The
/// at-least-one-non-null rule is the shim's job, but the ingester re-checks it defensively.
/// </para>
/// <para>
/// AOT-safe: deserialized only via the source-generated <see cref="WatchdogJsonContext"/> (registered
/// there) — never a reflection-based <c>JsonSerializer.Deserialize&lt;T&gt;</c> overload.
/// </para>
/// </summary>
internal sealed class PlayerPresenceLine
{
    /// <summary>The event kind on the wire: <c>player_joined</c> or <c>player_left</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Opaque, game-scoped stable player id (SteamID64 / MC UUID), or null when unavailable.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Display name, or null when unavailable.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// ISO-8601-UTC timestamp the shim recorded. Carried for completeness/diagnostics only — the wire
    /// event is timestamped fresh by kgsm at emit time (the shim clock is not the authority), so this
    /// is deliberately a free-form string and not re-emitted.
    /// </summary>
    [JsonPropertyName("ts")]
    public string? Ts { get; set; }
}
