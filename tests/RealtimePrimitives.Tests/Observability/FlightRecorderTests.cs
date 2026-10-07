using System.Text.Json;
using ShadowPrompter.Core.Observability.Flight;
using ShadowPrompter.Tests.TestDoubles;
using Xunit;

namespace ShadowPrompter.Tests.Observability;

/// <summary>Collects what the recorder hands to a sink (and can be made slow or broken).</summary>
internal sealed class ListFlightSink : IFlightSink
{
    private readonly List<string> _lines = [];
    private readonly Lock _lock = new();

    public TimeSpan Delay { get; set; }

    public bool Throws { get; set; }

    public bool Flushed { get; private set; }

    public bool Disposed { get; private set; }

    public string Name => "list";

    public List<string> Lines
    {
        get
        {
            lock (_lock) return [.. _lines];
        }
    }

    public List<JsonDocument> Events => [.. Lines.Select(l => JsonDocument.Parse(l))];

    public void Write(in FlightRecord record)
    {
        if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
        if (Throws) throw new IOException("disk full");
        lock (_lock) _lines.Add(System.Text.Encoding.UTF8.GetString(record.Json.Span));
    }

    public void Flush() => Flushed = true;

    public void Dispose() => Disposed = true;
}

public class FlightRecorderTests
{
    private static async Task<List<JsonDocument>> RecordAsync(Action<FlightRecorder> act, bool includeText = true, int capacity = 4096)
    {
        var sink = new ListFlightSink();
        var recorder = new FlightRecorder("abc12345", [sink], includeText, capacity);
        act(recorder);
        await recorder.DisposeAsync();
        return sink.Events;
    }

    [Fact]
    public async Task An_Event_Carries_The_Envelope_And_Its_Own_Fields()
    {
        var events = await RecordAsync(r => r.Begin("answer.end").Gen(7).Num("ms", 1234L).Num("ratio", 0.12345, 2).Flag("ok", true).Str("outcome", "committed").Emit());

        var e = Assert.Single(events).RootElement;
        Assert.Equal("answer.end", e.GetProperty("k").GetString());
        Assert.Equal("abc12345", e.GetProperty("s").GetString());
        Assert.Equal(1, e.GetProperty("n").GetInt64());
        Assert.Equal(7, e.GetProperty("gen").GetInt64());
        Assert.True(e.GetProperty("mono").GetInt64() >= 0); // the envelope's monotonic time ...
        Assert.Equal(1234, e.GetProperty("ms").GetInt64()); // ... and a field called ms are two different things
        Assert.Equal(0.12, e.GetProperty("ratio").GetDouble());
        Assert.True(e.GetProperty("ok").GetBoolean());
        Assert.Equal("committed", e.GetProperty("outcome").GetString());
        Assert.EndsWith("Z", e.GetProperty("t").GetString());
    }

    [Fact]
    public async Task A_Field_Never_Reuses_A_Key_Of_The_Envelope_Or_Of_The_Ambient_Context()
    {
        var sink = new ListFlightSink();
        await using var recorder = new FlightRecorder("s", [sink]);
        using (Flight.Scope(recorder))
        using (FlightContext.Enter("answer", 4))
        using (FlightContext.EnterTarget("p/m#k"))
            Flight.Event("x").Num("mono", 5L).Str("k", "other").Str("target", "hedge-target").Num("gen", 99L).Str("pipe", "z").Emit();
        await recorder.DisposeAsync();

        var line = Assert.Single(sink.Lines);
        var e = JsonDocument.Parse(line).RootElement; // a duplicate key would make a reader keep the wrong one
        Assert.Equal("x", e.GetProperty("k").GetString());
        Assert.Equal(4, e.GetProperty("gen").GetInt64());
        Assert.Equal("p/m#k", e.GetProperty("target").GetString());
        Assert.Equal("answer", e.GetProperty("pipe").GetString());
        Assert.Equal(5, e.GetProperty("mono_").GetInt64());
        Assert.Equal("other", e.GetProperty("k_").GetString());
        Assert.Equal("hedge-target", e.GetProperty("target_").GetString());
        Assert.Equal(99, e.GetProperty("gen_").GetInt64());
        Assert.Equal("z", e.GetProperty("pipe_").GetString());
        Assert.Equal(1, line.Split("\"k\":").Length - 1); // exactly one "k" key
    }

    [Fact]
    public async Task Russian_Text_Stays_Readable_And_Is_Not_Escaped()
    {
        var sink = new ListFlightSink();
        var recorder = new FlightRecorder("s", [sink]);
        recorder.Begin("stt.turn").Text("text", "Как бы ты выполнял «независимые» запросы?").Emit();
        await recorder.DisposeAsync();

        var line = Assert.Single(sink.Lines);
        Assert.Contains("Как бы ты выполнял «независимые» запросы?", line);
        Assert.DoesNotContain("\\u", line);
    }

    [Fact]
    public async Task Without_Text_Only_The_Length_Is_Recorded()
    {
        var events = await RecordAsync(r => r.Begin("stt.turn").Text("text", "секретный ответ").Emit(), includeText: false);

        var e = Assert.Single(events).RootElement;
        Assert.Equal(15, e.GetProperty("text_len").GetInt32());
        Assert.False(e.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task A_Very_Long_Text_Is_Cut_With_A_Marker()
    {
        var events = await RecordAsync(r => r.Begin("answer.end").Text("text", new string('я', FlightEvent.MaxTextChars + 500)).Emit());

        var e = Assert.Single(events).RootElement;
        Assert.Equal(FlightEvent.MaxTextChars + 500, e.GetProperty("text_len").GetInt32());
        Assert.EndsWith("…[cut]", e.GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_Failure_Is_Recorded_By_Type_And_Cut_Message()
    {
        var events = await RecordAsync(r => r.Begin("answer.fail").Error(new IOException("connection reset", new TimeoutException())).Emit());

        var e = Assert.Single(events).RootElement;
        Assert.Equal("IOException", e.GetProperty("error_type").GetString());
        Assert.Equal("connection reset", e.GetProperty("error").GetString());
        Assert.Equal("TimeoutException", e.GetProperty("error_inner").GetString());
    }

    [Fact]
    public async Task Events_Reach_The_Sink_In_Order_With_Increasing_Sequence_Numbers()
    {
        var events = await RecordAsync(r =>
        {
            for (var i = 0; i < 200; i++) r.Begin("x").Num("i", i).Emit();
        });

        Assert.Equal(200, events.Count);
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(i, events[i].RootElement.GetProperty("i").GetInt32());
            Assert.Equal(i + 1, events[i].RootElement.GetProperty("n").GetInt64());
        }
    }

    [Fact]
    public async Task An_Event_Emitted_Twice_Is_Recorded_Once()
    {
        var events = await RecordAsync(r =>
        {
            var e = r.Begin("once");
            e.Emit();
            e.Emit();
        });

        Assert.Single(events);
    }

    [Fact]
    public async Task A_Slow_Sink_Does_Not_Block_The_Caller_And_The_Overflow_Is_Counted_And_Visible()
    {
        var sink = new ListFlightSink { Delay = TimeSpan.FromMilliseconds(30) };
        var recorder = new FlightRecorder("s", [sink], capacity: 64);

        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++) recorder.Begin("burst").Num("i", i).Emit();
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), "emitting must not wait for the sink");

        await recorder.DisposeAsync();

        Assert.True(recorder.Dropped > 0);
        var kinds = sink.Events.Select(d => d.RootElement.GetProperty("k").GetString()).ToList();
        Assert.Contains("flight.dropped", kinds); // the hole announces itself
        Assert.True(sink.Lines.Count < 2000);
    }

    [Fact]
    public async Task A_Sink_That_Throws_Costs_The_Others_Nothing()
    {
        var broken = new ListFlightSink { Throws = true };
        var good = new ListFlightSink();
        var recorder = new FlightRecorder("s", [broken, good]);

        recorder.Begin("a").Emit();
        recorder.Begin("b").Emit();
        await recorder.DisposeAsync();

        Assert.Equal(2, good.Lines.Count);
        Assert.True(good.Flushed);
        Assert.True(good.Disposed);
    }

    [Fact]
    public async Task Disposing_Writes_What_Is_Already_Queued_And_Later_Events_Are_Ignored()
    {
        var sink = new ListFlightSink();
        var recorder = new FlightRecorder("s", [sink]);
        for (var i = 0; i < 50; i++) recorder.Begin("before").Emit();
        await recorder.DisposeAsync();

        recorder.Begin("after").Emit();

        Assert.Equal(50, sink.Lines.Count);
        Assert.DoesNotContain(sink.Lines, l => l.Contains("\"after\""));
    }

    [Fact]
    public void The_Null_Recorder_Does_Nothing_And_Allocates_No_Builder()
    {
        var e = NullFlightRecorder.Instance.Begin("x");

        Assert.False(e.Active);
        e.Num("a", 1L).Str("b", "c").Text("d", "e").Error(new Exception("x")).Emit(); // must not throw
    }

    [Fact]
    public async Task A_Scoped_Recorder_Is_Seen_Only_By_Its_Own_Flow_And_The_Previous_One_Returns()
    {
        var sink = new ListFlightSink();
        var recorder = new FlightRecorder("scoped", [sink]);

        Assert.False(Flight.On); // nothing installed: free
        using (Flight.Scope(recorder))
        {
            Assert.True(Flight.On);
            Flight.Event("inside").Emit();
        }

        Assert.False(Flight.On);
        await recorder.DisposeAsync();
        Assert.Single(sink.Lines);
    }

    [Fact]
    public async Task A_Scope_Flows_Into_Tasks_Started_Inside_It()
    {
        var sink = new ListFlightSink();
        await using var recorder = new FlightRecorder("scoped", [sink]);

        using (Flight.Scope(recorder))
            await Task.Run(() => Flight.Event("from.task").Emit(), TestContext.Current.CancellationToken);

        await TestWait.UntilAsync(() => sink.Lines.Count == 1, "the event from the task");
    }

    [Fact]
    public async Task An_Event_In_An_Activity_Carries_Its_Trace_And_Span_Ids()
    {
        using var source = new System.Diagnostics.ActivitySource("test.flight");
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == "test.flight",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var sink = new ListFlightSink();
        await using (var recorder = new FlightRecorder("s", [sink]))
        {
            string traceId;
            using (var activity = source.StartActivity("work"))
            {
                Assert.NotNull(activity);
                traceId = activity!.TraceId.ToHexString();
                recorder.Begin("in.span").Emit();
            }

            await TestWait.UntilAsync(() => sink.Lines.Count == 1, "the event");
            Assert.Equal(traceId, sink.Events[0].RootElement.GetProperty("trace").GetString());
        }
    }
}

public class JsonlFlightSinkTests
{
    [Fact]
    public async Task Events_Are_Appended_As_Lines_That_Survive_A_Reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), "sp-flight-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            await using (var first = new FlightRecorder("s1", [new JsonlFlightSink(path)]))
                first.Begin("one").Emit();
            await using (var second = new FlightRecorder("s2", [new JsonlFlightSink(path)]))
                second.Begin("two").Emit();

            var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(2, lines.Length);
            Assert.Equal("one", JsonDocument.Parse(lines[0]).RootElement.GetProperty("k").GetString());
            Assert.Equal("s2", JsonDocument.Parse(lines[1]).RootElement.GetProperty("s").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_File_That_Reaches_Its_Cap_Stops_And_Says_So_Once()
    {
        var path = Path.Combine(Path.GetTempPath(), "sp-flight-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            await using (var recorder = new FlightRecorder("s", [new JsonlFlightSink(path, maxBytes: 600)]))
                for (var i = 0; i < 100; i++) recorder.Begin("spam").Str("pad", new string('x', 50)).Emit();

            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(1, text.Split("flight.capped").Length - 1);
            Assert.True(new FileInfo(path).Length < 1500);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
