using System.Globalization;
using System.Text.Json;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Services;

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
/// <b>Writes are inline, not off-thread.</b> An append is a single write to a file this daemon owns,
/// so doing it in place is fast enough and strictly better: the journal's order becomes the order
/// things happened, where hopping to the thread pool lets two events emitted back to back land the
/// wrong way round.
/// </para>
/// <para>
/// <b>Best-effort, and never fatal.</b> A failed write is logged and reported back; supervision
/// continues. A dropped event is the same honest no-backfill boundary consumers already accept, and
/// failing an operation that has already happened because recording it did not work would be the
/// worse trade.
/// </para>
/// <para>
/// The payload shapes live here and nowhere else, so the four places that emit cannot drift into
/// describing the same event two ways.
/// </para>
/// </remarks>
public sealed class WatchdogJournal(IEventJournalWriter writer, ILogger<WatchdogJournal> logger)
    : JournalRecorder(writer, logger)
{
    /// <summary>
    /// This daemon's producer id — its state directory's own name, and the single input from which its
    /// journal location, its stamped version and its actor are all derived.
    /// </summary>
    public const string ProducerId = "kgsm-watchdog";

    /// <summary>This daemon's identity on an action it took by itself.</summary>
    /// <remarks>
    /// <c>provider:name</c> is the actor convention every consumer parses: a bare name reads as an OS
    /// user (kgsm-api treats an unprefixed actor as a person on the local host), so an autonomous
    /// emitter has to name its identity source. Derived from <see cref="ProducerId"/> rather than
    /// spelled out, so this daemon's identity has one source — and exposed because the supervisor
    /// hands it to the firewall authority as the provenance of a change it asked for.
    /// </remarks>
    public static readonly string ActorWatchdog = JournalProducer.SystemActorFor(ProducerId);

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

    /// <summary>
    /// Appends one event, naming the instance it was about.
    /// </summary>
    /// <remarks>
    /// The base handles the append itself — type normalisation, the derived actor, and a failure that
    /// is logged rather than thrown. This adds the one thing it cannot know: which instance the line
    /// was about, which is what makes the debug trail readable when several are moving at once.
    /// </remarks>
    private void Write(string eventType, string instanceName, string? actor, Action<Utf8JsonWriter> data)
    {
        // Parsed here rather than declared as a constant, because several of these names arrive from a
        // parser reading a container's own output — the one place in this daemon where the name is not
        // known until it is read. A name that is not a name is dropped loudly: writing it would put a
        // line on the journal that no consumer matches, which fails silently everywhere downstream.
        if (!EventName.TryParse(eventType, out EventName name))
        {
            logger.LogError(
                "'{Event}' is not a valid event name; the line for {Instance} was not written",
                eventType, instanceName);
            return;
        }

        // How much the event matters and how it went come from the engine's own catalog, which is
        // where its payload type and its fields are already declared — so this daemon states a weight
        // it was told rather than one it invented, and an event gains its weight in one place.
        //
        // Only for a type the catalog recognises. Stamping the defaults onto an event nobody has
        // classified would assert a weight nothing established, and absent is what "unknown" is
        // spelled as.
        EventDescriptor descriptor = KgsmEventCatalog.Describe(name.Value);
        EventSeverity? severity = descriptor.Known ? descriptor.Severity : null;
        EventOutcome? outcome = descriptor.Known ? descriptor.Outcome : null;

        if (Record(name, data, actor, severity: severity, outcome: outcome))
            logger.LogDebug("recorded {Event} for {Instance}", name.Value, instanceName);
    }
}
