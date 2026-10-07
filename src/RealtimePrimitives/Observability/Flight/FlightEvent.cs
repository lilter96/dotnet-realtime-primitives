using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ShadowPrompter.Core.Observability.Flight;

/// <summary>
/// A fluent builder of ONE event: <c>Flight.Event("answer.end").Gen(7).Num("ms", 1234).Str("outcome", "committed").Emit()</c>. The JSON is
/// written as the fields arrive (<see cref="Utf8JsonWriter"/>: no reflection, nothing for a serializer context to know about). A disabled
/// recorder hands out <c>default</c>, whose every method does nothing, so an instrumented hot path costs one branch.
/// </summary>
public readonly struct FlightEvent
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Russian text must stay readable in the file and in Loki (\uXXXX escapes made a request 2.5x larger once already).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true
    };

    /// <summary>A text field longer than this is cut (with a marker): an answer is ~1 000 characters, a whole prompt is not an event.</summary>
    public const int MaxTextChars = 6000;

    private readonly Buffer? _buffer;

    internal FlightEvent(IFlightRecorder owner, string kind, DateTimeOffset at, long sequence, string session, long monotonicMs)
    {
        _buffer = new Buffer(owner, kind, at, sequence);
        var w = _buffer.Writer;
        w.WriteStartObject();
        w.WriteString("t", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        w.WriteNumber("mono", monotonicMs);
        w.WriteNumber("n", sequence);
        w.WriteString("s", session);
        w.WriteString("k", kind);
        if (FlightContext.Current is { } context)
        {
            // The answer run this flow belongs to (see FlightContext): every event below it is tagged without being told.
            _buffer.Generation = context.Generation;
            _buffer.GenerationWritten = true;
            w.WriteNumber("gen", context.Generation);
            w.WriteString("pipe", context.Pipeline);
            _buffer.ContextWritten = true;
            _buffer.WroteTarget = context.Target is not null;
            if (context.Target is not null) w.WriteString("target", context.Target);
        }

        if (Activity.Current is { } activity && activity.IdFormat == ActivityIdFormat.W3C)
        {
            _buffer.TraceId = activity.TraceId.ToHexString();
            _buffer.SpanId = activity.SpanId.ToHexString();
            w.WriteString("trace", _buffer.TraceId);
            w.WriteString("span", _buffer.SpanId);
        }
    }

    // The keys of the envelope: no field of an event may reuse one (two "ms" in one object was the first bug of this format: a duration
    // named ms silently replaced the monotonic time in every reader that keeps the last duplicate).
    private static readonly HashSet<string> EnvelopeKeys = new(StringComparer.Ordinal) { "t", "mono", "n", "s", "k", "trace", "span" };

    /// <summary>The name a field is written under: its own, unless that would duplicate a key already in the object (then it gets a trailing underscore).</summary>
    private string Key(string name)
    {
        var b = _buffer!;
        if (EnvelopeKeys.Contains(name) || (b.GenerationWritten && name == "gen") || (b.ContextWritten && name is "pipe" or "target" && b.Reserved(name)))
            return name + "_";
        return name;
    }

    /// <summary>True when this event will be recorded (so a caller may skip building an expensive field).</summary>
    public bool Active => _buffer is not null;

    /// <summary>The answer generation (voice and vision runs share one numbering per pipeline): the key to join an answer's events.</summary>
    public FlightEvent Gen(long generation)
    {
        if (_buffer is null || _buffer.GenerationWritten) return this; // the ambient context already tagged the event
        _buffer.Generation = generation;
        _buffer.GenerationWritten = true;
        _buffer.Writer.WriteNumber("gen", generation);
        return this;
    }

    public FlightEvent Str(string name, string? value)
    {
        if (_buffer is null) return this;
        if (value is null) _buffer.Writer.WriteNull(Key(name));
        else _buffer.Writer.WriteString(Key(name), value);
        return this;
    }

    public FlightEvent Num(string name, long value)
    {
        if (_buffer is not null) _buffer.Writer.WriteNumber(Key(name), value);
        return this;
    }

    /// <summary>A measurement with a fractional part, rounded to <paramref name="decimals"/> (a file of 17-digit doubles helps nobody).</summary>
    public FlightEvent Num(string name, double value, int decimals = 1)
    {
        if (_buffer is null) return this;
        if (double.IsNaN(value) || double.IsInfinity(value)) _buffer.Writer.WriteNull(Key(name));
        else _buffer.Writer.WriteNumber(Key(name), Math.Round(value, decimals));
        return this;
    }

    public FlightEvent Flag(string name, bool value)
    {
        if (_buffer is not null) _buffer.Writer.WriteBoolean(Key(name), value);
        return this;
    }

    /// <summary>A duration in milliseconds, one decimal.</summary>
    public FlightEvent Dur(string name, TimeSpan value) => Num(name, value.TotalMilliseconds);

    /// <summary>
    /// A text (transcript, answer, question). Its length is always recorded as <c>{name}_len</c>; the text itself only when the recorder
    /// includes texts, cut at <see cref="MaxTextChars"/>.
    /// </summary>
    public FlightEvent Text(string name, string? value)
    {
        if (_buffer is null) return this;
        var length = value?.Length ?? 0;
        _buffer.Writer.WriteNumber(Key(name + "_len"), length);
        if (value is null || !_buffer.Owner.IncludeText) return this;
        _buffer.Writer.WriteString(Key(name), value.Length <= MaxTextChars ? value : string.Concat(value.AsSpan(0, MaxTextChars), " …[cut]"));
        return this;
    }

    /// <summary>The type and (cut) message of a failure, and its inner failure's type when the real cause is one level down.</summary>
    public FlightEvent Error(Exception? exception)
    {
        if (_buffer is null || exception is null) return this;
        _buffer.Writer.WriteString("error_type", exception.GetType().Name);
        var message = Logging.Secrets.Redact(exception.Message);
        _buffer.Writer.WriteString("error", message.Length <= 300 ? message : message[..300] + "…");
        if (exception.InnerException is { } inner) _buffer.Writer.WriteString("error_inner", inner.GetType().Name);
        return this;
    }

    /// <summary>Records the event. Safe to call once; a second call is ignored.</summary>
    public void Emit()
    {
        var buffer = _buffer;
        if (buffer is null || buffer.Done) return;
        buffer.Done = true;
        try
        {
            buffer.Writer.WriteEndObject();
            buffer.Writer.Flush();
            buffer.Owner.Enqueue(new FlightRecord(
                buffer.Kind, buffer.At, buffer.Sequence, buffer.Sink.WrittenMemory, buffer.Generation, buffer.TraceId, buffer.SpanId));
        }
        catch
        {
            // Telemetry never breaks the thing it observes.
        }
    }

    private sealed class Buffer
    {
        public Buffer(IFlightRecorder owner, string kind, DateTimeOffset at, long sequence)
        {
            Owner = owner;
            Kind = kind;
            At = at;
            Sequence = sequence;
            Sink = new ArrayBufferWriter<byte>(384);
            Writer = new Utf8JsonWriter(Sink, WriterOptions);
        }

        public IFlightRecorder Owner { get; }
        public string Kind { get; }
        public DateTimeOffset At { get; }
        public long Sequence { get; }
        public ArrayBufferWriter<byte> Sink { get; }
        public Utf8JsonWriter Writer { get; }
        public long Generation { get; set; }
        public bool GenerationWritten { get; set; }
        public bool ContextWritten { get; set; }
        public bool WroteTarget { get; set; }

        /// <summary>Whether the ambient context already wrote this key into the object.</summary>
        public bool Reserved(string name) => name == "pipe" || (name == "target" && WroteTarget);
        public string? TraceId { get; set; }
        public string? SpanId { get; set; }
        public bool Done { get; set; }
    }
}
