using System.Text.Json;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The pure, side-effect-free translation of one raw <c>lifecycle.ndjson</c> line (schema
/// <see cref="ContainerLifecycleLine"/>) into a recognised lifecycle type — or a decision to drop it.
/// Mirrors <see cref="PlayerPresenceParser"/>'s shape/rules (kept separate from the ingester so every
/// branch — valid started/stopping, malformed JSON, unknown type — is unit-testable without files or
/// kgsm-lib).
/// </summary>
internal static class ContainerLifecycleParser
{
    internal const string TypeStarted = "instance_started";
    internal const string TypeStopping = "instance_stopping";

    /// <summary>
    /// The outcome of parsing one line: either a recognised <see cref="Type"/> to act on
    /// (<see cref="Emit"/> true), or a drop with a human <see cref="DropReason"/>.
    /// </summary>
    internal readonly record struct ParseResult(bool Emit, string? Type, string? DropReason)
    {
        public static ParseResult Drop(string reason) => new(false, null, reason);
        public static ParseResult Event(string type) => new(true, type, null);
    }

    /// <summary>
    /// Parse one NDJSON line. Blank lines drop silently (a partial write / trailing newline is normal,
    /// not an error). A malformed line or an unknown/absent <c>type</c> drops with a reason for the
    /// caller to log.
    /// </summary>
    public static ParseResult Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return ParseResult.Drop("blank line");

        ContainerLifecycleLine? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(line, WatchdogJsonContext.Default.ContainerLifecycleLine);
        }
        catch (JsonException ex)
        {
            return ParseResult.Drop($"malformed JSON: {ex.Message}");
        }

        if (parsed is null)
            return ParseResult.Drop("JSON null");

        return parsed.Type switch
        {
            TypeStarted => ParseResult.Event(TypeStarted),
            TypeStopping => ParseResult.Event(TypeStopping),
            _ => ParseResult.Drop($"unknown or missing type '{parsed.Type}'"),
        };
    }
}
