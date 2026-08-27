using System.Text.Json;
using TheKrystalShip.KGSM.Watchdog.Model;

namespace TheKrystalShip.KGSM.Watchdog.Supervision;

/// <summary>
/// The pure, side-effect-free translation of one raw NDJSON channel line (schema
/// <see cref="PlayerPresenceLine"/>) into the kgsm wire event to emit — or a decision to drop it.
/// Kept separate from the ingester so every branch (valid join/left, malformed JSON, unknown type,
/// both-null id/name) is unit-testable without files, sockets, or kgsm-lib.
/// <para>
/// <b>Defensive at-least-one-non-null guard.</b> The shim is contracted to never write a line with
/// both <c>id</c> and <c>name</c> null, but the watchdog never trusts its input: a both-null line is
/// dropped here (the caller logs it), never emitted as a useless <c>{null,null}</c> presence event.
/// </para>
/// </summary>
internal static class PlayerPresenceParser
{
    // The two schema-B line types, and the events they map to.
    //
    // ⚠ The two are different vocabularies and only one of them is ours. A line type is what the
    // in-container shim writes to its own channel, so it is whatever that shim spells — it does not
    // move when the journal's vocabulary does. The event name is what this daemon then records.
    internal const string LineTypeJoined = "player_joined";
    internal const string LineTypeLeft = "player_left";
    internal const string EventPlayerJoined = "player.joined";
    internal const string EventPlayerLeft = "player.left";

    /// <summary>
    /// The outcome of parsing one line: either an event to emit (<see cref="Emit"/> true) with its
    /// dash-form <see cref="EventName"/> and the positional emit params, or a drop with a human
    /// <see cref="DropReason"/>. <see cref="PlayerId"/>/<see cref="PlayerName"/> are the raw captures
    /// (null ⇒ unavailable, surfaced to the emit layer as an empty positional arg — never fabricated).
    /// <see cref="PlayerAddr"/>/<see cref="Key"/>/<see cref="Reason"/> are native-matcher-only additions
    /// (player-presence contract §4): the container NDJSON schema (this parser's input) doesn't carry
    /// them yet, so they stay null on every result this type produces — <see cref="NativeLogMatcher"/>
    /// is the one that populates them, reusing this same result shape per its doc comment.
    /// </summary>
    internal readonly record struct ParseResult(
        bool Emit,
        string? EventName,
        string? PlayerId,
        string? PlayerName,
        string? PlayerAddr,
        string? Key,
        string? Reason,
        string? DropReason)
    {
        public static ParseResult Drop(string reason) => new(false, null, null, null, null, null, null, reason);

        public static ParseResult Event(
            string eventName, string? id, string? name, string? addr = null, string? key = null, string? reason = null)
            => new(true, eventName, id, name, addr, key, reason, null);
    }

    /// <summary>
    /// Parse one NDJSON line. Blank lines drop silently (a partial write / trailing newline is normal,
    /// not an error). A malformed line, an unknown/absent <c>type</c>, or a both-null id+name all drop
    /// with a reason for the caller to log. Whitespace-only id/name are treated as absent.
    /// </summary>
    public static ParseResult Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return ParseResult.Drop("blank line");

        PlayerPresenceLine? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(line, WatchdogJsonContext.Default.PlayerPresenceLine);
        }
        catch (JsonException ex)
        {
            return ParseResult.Drop($"malformed JSON: {ex.Message}");
        }

        if (parsed is null)
            return ParseResult.Drop("JSON null");

        string? eventName = parsed.Type switch
        {
            LineTypeJoined => EventPlayerJoined,
            LineTypeLeft => EventPlayerLeft,
            null or "" => null,
            _ => null,
        };
        if (eventName is null)
            return ParseResult.Drop($"unknown or missing type '{parsed.Type}'");

        // Normalise whitespace-only captures to null, then enforce the at-least-one-non-null rule.
        string? id = NormaliseField(parsed.Id);
        string? name = NormaliseField(parsed.Name);
        if (id is null && name is null)
            return ParseResult.Drop("both id and name are null/empty (at-least-one-non-null violated)");

        return ParseResult.Event(eventName, id, name);
    }

    /// <summary>An empty or whitespace-only field is treated as absent (null), so it can't sneak past the guard.</summary>
    private static string? NormaliseField(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
