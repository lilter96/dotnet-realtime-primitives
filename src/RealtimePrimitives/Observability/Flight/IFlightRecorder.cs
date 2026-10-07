namespace ShadowPrompter.Core.Observability.Flight;

/// <summary>
/// One recorded event, already serialized: the whole JSON line (the envelope <c>t</c>/<c>ms</c>/<c>n</c>/<c>s</c>/<c>k</c> first, then
/// the event's own fields) plus the few values a sink may want as separate attributes without parsing it.
/// </summary>
/// <param name="Kind">Dotted event name, <c>answer.end</c>, <c>stt.conn</c>…: the vocabulary is documented in <c>docs/observability.md</c>.</param>
/// <param name="At">Wall-clock time of the event.</param>
/// <param name="Sequence">Strictly increasing within one session: a reader can tell a gap (dropped events) from a quiet minute.</param>
/// <param name="Json">The complete line, no trailing newline.</param>
/// <param name="Generation">The answer generation the event belongs to, or 0.</param>
/// <param name="TraceId">The distributed-trace id of the span the event happened in, when one was active.</param>
public readonly record struct FlightRecord(
    string Kind, DateTimeOffset At, long Sequence, ReadOnlyMemory<byte> Json, long Generation = 0, string? TraceId = null, string? SpanId = null);

/// <summary>Where recorded events end up: a file, the OTLP log pipeline (Loki), a Redis stream. Called from ONE background thread, in order.</summary>
public interface IFlightSink : IDisposable
{
    string Name { get; }

    /// <summary>Takes one event. Must not block for long and must not throw (the recorder counts and survives it, but a sink that always throws is dead weight).</summary>
    void Write(in FlightRecord record);

    /// <summary>Pushes whatever the sink buffers (called about once a second, and at the end).</summary>
    void Flush();
}

/// <summary>
/// The black box of a session: every part of the application tells it what happened — structurally, with timings — and it hands the events
/// to its sinks on a background thread, so an interview never waits on a disk, a socket or an exporter. Off = <see cref="NullFlightRecorder"/>:
/// <see cref="Begin"/> then returns an inert builder and costs nothing.
/// </summary>
public interface IFlightRecorder
{
    bool Enabled { get; }

    /// <summary>Whether texts (transcripts, answers) go into events; false keeps only their lengths.</summary>
    bool IncludeText { get; }

    /// <summary>Starts an event; nothing is recorded until <see cref="FlightEvent.Emit"/>.</summary>
    FlightEvent Begin(string kind);

    /// <summary>Enqueues a finished event (used by <see cref="FlightEvent.Emit"/>).</summary>
    void Enqueue(in FlightRecord record);
}

/// <summary>The recorder of a process that does not record: nothing allocates, nothing is written.</summary>
public sealed class NullFlightRecorder : IFlightRecorder
{
    public static readonly NullFlightRecorder Instance = new();

    private NullFlightRecorder()
    {
    }

    public bool Enabled => false;

    public bool IncludeText => false;

    public FlightEvent Begin(string kind) => default;

    public void Enqueue(in FlightRecord record)
    {
    }
}
