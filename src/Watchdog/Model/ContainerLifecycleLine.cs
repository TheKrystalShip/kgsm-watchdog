using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Watchdog.Model;

/// <summary>
/// One line of the in-container → host <b>lifecycle</b> event channel — the NDJSON schema the
/// kgsm-containers in-image management script writes (Bucket 2/3 groundwork, distinct from
/// <see cref="PlayerPresenceLine"/>'s <c>events.ndjson</c>, though it shares the same bind-mounted
/// <c>/run/kgsm</c> channel dir). Schema:
/// <code>
/// {"type":"server.started","ts":"&lt;ISO-8601-UTC&gt;"}
/// {"type":"instance_stopping","ts":"&lt;ISO-8601-UTC&gt;"}
/// </code>
/// <para>
/// <see cref="Ts"/> is carried for completeness/diagnostics only, same convention as
/// <see cref="PlayerPresenceLine.Ts"/> — never re-emitted as an authoritative timestamp.
/// </para>
/// <para>
/// AOT-safe: deserialized only via the source-generated <see cref="WatchdogJsonContext"/> (registered
/// there) — never a reflection-based <c>JsonSerializer.Deserialize&lt;T&gt;</c> overload.
/// </para>
/// </summary>
internal sealed class ContainerLifecycleLine
{
    /// <summary>The event kind on the wire: <c>instance_started</c> or <c>instance_stopping</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>ISO-8601-UTC timestamp the in-container script recorded. Diagnostics only.</summary>
    [JsonPropertyName("ts")]
    public string? Ts { get; set; }
}
