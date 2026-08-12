using System.Globalization;
using System.Text.Json;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Watchdog.Events;

/// <summary>
/// Records what this daemon did, in this daemon's own event journal.
/// </summary>
/// <remarks>
/// <para>
/// <b>A producer emits what that producer does.</b> Every event here is one the watchdog itself
/// established — it spawned the process, it opened the port, it saw the readiness line — so it writes
/// them, rather than asking the engine to write them down on its behalf. That also removes a
/// <c>kgsm.sh</c> process spawn per event: a bash bootstrap, a sourced library and a <c>jq</c> call to
/// append one line, three times over on a single server start.
/// </para>
/// <para>
/// <b>Writes are inline, not off-thread.</b> The old path hopped to the thread pool because an engine
/// spawn is slow enough to stall the reconcile tick, and that hop meant two events emitted back to back
/// could land out of order. An append is a single write to a file this daemon owns, so doing it in
/// place is both fast enough and strictly better: the journal's order becomes the order things
/// happened.
/// </para>
/// <para>
/// <b>Best-effort, and never fatal.</b> A failed write is logged by the writer and reported back as
/// false; supervision continues. A dropped event is the same honest no-backfill boundary consumers
/// already accept, and failing an operation that has already happened because recording it did not work
/// would be the worse trade.
/// </para>
/// <para>
/// The payload shapes live here and nowhere else, so the four places that emit cannot drift into
/// describing the same event two ways.
/// </para>
/// </remarks>
public sealed class WatchdogJournal(IEventJournalWriter writer, ILogger<WatchdogJournal> logger)
{
    /// <summary>This daemon's identity, and the origin of an action no human surface drove.</summary>
    /// <remarks>
    /// <c>provider:name</c> is the actor convention every consumer parses: a bare name reads as an OS
    /// user (kgsm-api treats an unprefixed actor as a person on the local host), so an autonomous
    /// emitter has to name its identity source.
    /// </remarks>
    public const string ActorWatchdog = "system:watchdog";

    /// <summary>The origin for an action no product surface drove.</summary>
    public const string OriginSystem = "system";

    /// <summary>
    /// An event whose whole payload is the instance it is about — started, restarted, ready.
    /// </summary>
    /// <param name="eventType">The event type, dash- or underscore-separated.</param>
    /// <param name="instanceName">The instance the event is about.</param>
    /// <param name="actor">Who to attribute it to. Defaults to this daemon.</param>
    public void Instance(string eventType, string instanceName, string? actor = null)
        => Write(eventType, instanceName, actor, w => w.WriteString("InstanceName", instanceName));

    /// <summary>
    /// A crash or a give-up: how the run ended, and how many attempts preceded it.
    /// </summary>
    /// <param name="eventType">The event type.</param>
    /// <param name="instanceName">The instance that crashed.</param>
    /// <param name="exitCode">
    /// The leader's exit code, or null where it could not be read — written as the literal
    /// <c>"unknown"</c>, never as a fabricated code.
    /// </param>
    /// <param name="restarts">The consecutive-failure streak at the moment the run ended.</param>
    public void Supervision(string eventType, string instanceName, int? exitCode, int restarts)
        => Write(eventType, instanceName, actor: null, w =>
        {
            w.WriteString("InstanceName", instanceName);
            w.WriteString("ExitCode", exitCode is int code ? code.ToString(CultureInfo.InvariantCulture) : "unknown");
            w.WriteString("Restarts", restarts.ToString(CultureInfo.InvariantCulture));
        });

    /// <summary>
    /// A firewall or router door that opened or closed, naming the ports it actually acted on.
    /// </summary>
    /// <remarks>
    /// The ports are written structured, straight from the mappings this daemon holds. The engine path
    /// rendered them down to a UFW string so kgsm could parse them back into exactly this shape, and
    /// removing that round-trip removes the only place the two spellings could disagree.
    /// </remarks>
    /// <param name="eventType">The event type.</param>
    /// <param name="instanceName">The instance whose ports moved.</param>
    /// <param name="ports">
    /// What actually changed — never the declared set when less than all of it was applied, or the
    /// trail would carry a transition that did not happen.
    /// </param>
    public void Ports(string eventType, string instanceName, IReadOnlyList<PortMapping> ports)
        => Write(eventType, instanceName, actor: null, w =>
        {
            w.WriteString("InstanceName", instanceName);
            w.WriteStartArray("Ports");

            foreach (PortMapping port in ports)
            {
                w.WriteStartObject();
                w.WriteNumber("start", port.Start);
                w.WriteNumber("end", port.End);
                w.WriteString("protocol", port.Protocol);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        });

    /// <summary>
    /// A player arriving or leaving, with whatever identified them.
    /// </summary>
    /// <remarks>
    /// The nullable fields are written as real JSON nulls. The engine path could not do that — a
    /// positional string emit cannot carry a null mid-arguments, so absent fields travelled as empty
    /// strings and kgsm mapped empty back to null at the far end. Writing the null directly means the
    /// honest-null rule no longer depends on a conversion happening two components away.
    /// </remarks>
    /// <param name="eventType">The event type.</param>
    /// <param name="instanceName">The server the player was on.</param>
    /// <param name="playerId">Their id, or null.</param>
    /// <param name="playerName">Their name, or null.</param>
    /// <param name="playerAddr">Their network address, or null.</param>
    /// <param name="sessionKey">The per-session correlation token. Always present.</param>
    /// <param name="reason">Why they left, or null — and null on a join, which has no reason.</param>
    public void Player(
        string eventType,
        string instanceName,
        string? playerId,
        string? playerName,
        string? playerAddr,
        string sessionKey,
        string? reason)
        => Write(eventType, instanceName, actor: null, w =>
        {
            w.WriteString("InstanceName", instanceName);
            WriteNullable(w, "PlayerId", playerId);
            WriteNullable(w, "PlayerName", playerName);
            WriteNullable(w, "PlayerAddr", playerAddr);
            w.WriteString("SessionKey", sessionKey);

            // Only the leave event carries a reason. A join has none, and writing an explicit null for
            // it would put a field in the payload that event's contract does not have.
            if (reason is not null)
                WriteNullable(w, "Reason", reason);
        });

    /// <summary>Writes a value, or a real JSON null when it carries nothing.</summary>
    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    /// <summary>
    /// Appends one event, normalising the type and never throwing.
    /// </summary>
    private void Write(string eventType, string instanceName, string? actor, Action<Utf8JsonWriter> data)
    {
        // Dash on the CLI, underscore on the wire — the engine's own convention, applied here because
        // the call sites name events the way the engine's command line does.
        string type = eventType.Replace('-', '_');

        try
        {
            bool written = writer
                .AppendAsync(type, data, actor ?? ActorWatchdog, OriginSystem)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            if (written)
                logger.LogDebug("recorded {Event} for {Instance}", type, instanceName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record {Event} for {Instance} (event dropped)", type, instanceName);
        }
    }
}
