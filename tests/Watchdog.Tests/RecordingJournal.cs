using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Watchdog.Events;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// A <see cref="WatchdogJournal"/> over an in-memory writer, capturing everything the daemon records.
/// </summary>
/// <remarks>
/// The real journal wrapped around a fake writer, rather than a fake journal. The payload shapes are the
/// part worth asserting — they are what a consumer deserializes — and a fake standing in for
/// <see cref="WatchdogJournal"/> itself would record the call and prove nothing about the JSON that
/// reaches the file.
/// </remarks>
public sealed class RecordingJournal
{
    private readonly RecordingWriter _writer = new();

    /// <summary>The journal to hand to the component under test.</summary>
    public WatchdogJournal Journal { get; }

    public RecordingJournal() => Journal = new WatchdogJournal(_writer, NullLogger<WatchdogJournal>.Instance);

    /// <summary>So a test can pass the recorder itself wherever the journal is expected.</summary>
    public static implicit operator WatchdogJournal(RecordingJournal recorder) => recorder.Journal;

    /// <summary>Every event recorded — the spelling the presence tests use.</summary>
    public IReadOnlyList<RecordedEvent> Calls => _writer.Recorded;

    /// <summary>Every event recorded, in order.</summary>
    public IReadOnlyList<RecordedEvent> Recorded => _writer.Recorded;

    /// <summary>The event types recorded, in order — the common assertion.</summary>
    public List<string> Emitted => [.. _writer.Recorded.Select(e => e.Type)];

    /// <summary>
    /// A stable copy of the recorded types, in the dash spelling the call sites use, so an assertion
    /// reads the same as the event name the daemon passes.
    /// </summary>
    public List<string> Snapshot() => [.. _writer.Recorded.Select(e => e.Type.Replace('_', '-'))];

    /// <summary>
    /// Waits briefly for an event type to be recorded. Accepts either spelling, because the call sites
    /// name events the way the engine's command line does and the journal writes the wire form.
    /// </summary>
    public bool WaitFor(string eventType, int timeoutMs = 2000)
    {
        string wanted = eventType.Replace('-', '_');
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (_writer.Recorded.Any(e => e.Type == wanted))
                return true;

            Thread.Sleep(10);
        }

        return false;
    }

    /// <summary>One recorded event: its type, provenance, and the payload as written.</summary>
    /// <param name="Type">The underscore-separated event type.</param>
    /// <param name="Actor">The actor stamped on it.</param>
    /// <param name="Origin">The origin stamped on it.</param>
    /// <param name="Data">The payload, parsed back from exactly what was written.</param>
    public sealed record RecordedEvent(string Type, string? Actor, string? Origin, JsonElement Data)
    {
        /// <summary>A payload string property, or null when it is absent or JSON null.</summary>
        public string? String(string property)
            => Data.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        /// <summary>Whether the payload carries <paramref name="property"/> as an explicit JSON null.</summary>
        public bool IsNull(string property)
            => Data.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Null;
    }

    private sealed class RecordingWriter : IEventJournalWriter
    {
        public List<RecordedEvent> Recorded { get; } = [];

        public string Producer => "kgsm-watchdog";

        public ValueTask<bool> AppendAsync(
            string eventType, JsonElement data, string? actor = null, string? origin = null,
            CancellationToken token = default)
        {
            Recorded.Add(new RecordedEvent(eventType, actor, origin, data.Clone()));
            return ValueTask.FromResult(true);
        }
    }
}
