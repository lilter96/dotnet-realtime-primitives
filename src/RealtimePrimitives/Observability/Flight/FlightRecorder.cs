using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ShadowPrompter.Core.Logging;

namespace ShadowPrompter.Core.Observability.Flight;

/// <summary>
/// The live recorder: events are numbered and serialized on the caller's thread (microseconds), then queued; ONE background task hands them
/// to every sink in order. The queue is bounded: when a sink falls behind for good, the OLDEST events are dropped and counted (a black box
/// that stalls the aircraft is worse than one with a hole), and the hole is visible — sequence numbers skip, and the next event says how many.
/// </summary>
public sealed class FlightRecorder : IFlightRecorder, IAsyncDisposable
{
    private static readonly Counter<long> EventsTotal = AppMetrics.Meter.CreateCounter<long>(
        "shadowprompter.flight.events", description: "Events handed to the flight recorder (tag: kind).");
    private static readonly Counter<long> DroppedTotal = AppMetrics.Meter.CreateCounter<long>(
        "shadowprompter.flight.dropped", description: "Events lost because the sinks fell behind (the queue is bounded).");
    private static readonly Counter<long> SinkFailures = AppMetrics.Meter.CreateCounter<long>(
        "shadowprompter.flight.sink_failures", description: "A sink threw while writing an event (tag: sink).");

    private readonly Channel<FlightRecord> _queue;
    private readonly IReadOnlyList<IFlightSink> _sinks;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _pump;
    private readonly CancellationTokenSource _cts = new();
    private long _sequence;
    private long _dropped;
    private long _reportedDropped;

    [ThreadStatic]
    private static bool t_directWrite;
    private readonly Lock _finalLock = new();
    private int _disposed;
    private int _finalizing;

    public FlightRecorder(
        string session, IReadOnlyList<IFlightSink> sinks, bool includeText = true, int capacity = 32768, TimeProvider? time = null, ILogger? log = null)
    {
        Session = session;
        IncludeText = includeText;
        _sinks = sinks;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _queue = Channel.CreateBounded<FlightRecord>(new BoundedChannelOptions(Math.Max(64, capacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        }, _ => Interlocked.Increment(ref _dropped));
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>The short id of this session; every event carries it, so lines from several sessions can share one Loki stream or Redis.</summary>
    public string Session { get; }

    public bool Enabled => true;

    public bool IncludeText { get; }

    /// <summary>Events lost to a full queue so far.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public FlightEvent Begin(string kind) => Volatile.Read(ref _disposed) != 0 ? default : Create(kind);

    private FlightEvent Create(string kind) =>
        new(this, kind, _time.GetUtcNow(), Interlocked.Increment(ref _sequence), Session, _clock.ElapsedMilliseconds);

    public void Enqueue(in FlightRecord record)
    {
        EventsTotal.Add(1, new KeyValuePair<string, object?>("kind", record.Kind));
        if (Volatile.Read(ref _finalizing) != 0 || t_directWrite)
        {
            // The pump is gone: the closing events (the last hole report, an event that was already being built) go straight to the sinks.
            // The same for a hole report made by the pump itself: it must not go back into a queue that is overflowing (the oldest event is
            // the one a full queue gives up, and a report waiting in it is the oldest as soon as the burst goes on: a stress run lost it).
            lock (_finalLock) WriteToSinks(record);
            return;
        }

        if (!_queue.Writer.TryWrite(record)) Interlocked.Increment(ref _dropped);
    }

    private async Task PumpAsync()
    {
        var lastFlush = Stopwatch.GetTimestamp();
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var record)) WriteToSinks(record);
                ReportDrops();
                if (Stopwatch.GetElapsedTime(lastFlush) > TimeSpan.FromSeconds(1))
                {
                    FlushSinks();
                    lastFlush = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: DisposeAsync drains what is left.
        }
        catch (Exception ex)
        {
            _log.Warn($"[flight] the recorder's pump stopped: {ex.Message}");
        }
    }

    private void WriteToSinks(in FlightRecord record)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Write(record);
            }
            catch (Exception ex)
            {
                SinkFailures.Add(1, new KeyValuePair<string, object?>("sink", sink.Name));
                _log.Warn($"[flight] sink {sink.Name} failed: {ex.Message}");
            }
        }
    }

    private void FlushSinks()
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Flush();
            }
            catch (Exception ex)
            {
                SinkFailures.Add(1, new KeyValuePair<string, object?>("sink", sink.Name));
                _log.Warn($"[flight] sink {sink.Name} failed to flush: {ex.Message}");
            }
        }
    }

    /// <summary>A hole in the record is itself an event: the reader must be able to tell "nothing happened" from "we lost it".</summary>
    private void ReportDrops()
    {
        // Counting a hole as reported and writing its report are ONE step under the lock the closing path also takes: when the pump lags past
        // the shutdown timeout, two threads can get here, and the one that marked the hole as reported must have written it before the
        // other (or Dispose) goes on to close the sinks. (A stress run lost the report between the mark and the write.)
        lock (_finalLock)
        {
            var dropped = Interlocked.Read(ref _dropped);
            if (dropped == _reportedDropped) return;
            DroppedTotal.Add(dropped - _reportedDropped);
            var gap = Create("flight.dropped").Num("count", dropped - _reportedDropped).Num("total", dropped);
            _reportedDropped = dropped;
            t_directWrite = true;
            try
            {
                gap.Emit();
            }
            finally
            {
                t_directWrite = false;
            }
        }
    }

    /// <summary>Stops accepting events, writes everything already queued, flushes and closes the sinks.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        // The pump leaves through WaitToRead returning false once the writer is complete and the queue empty; anything it did not reach is
        // written here, so the last events of a session (the summary) are never the ones that go missing.
        Volatile.Write(ref _finalizing, 1);
        lock (_finalLock)
        {
            while (_queue.Reader.TryRead(out var record)) WriteToSinks(record);
            ReportDrops();
            FlushSinks();
        }

        foreach (var sink in _sinks)
        {
            try
            {
                sink.Dispose();
            }
            catch
            {
                // Closing a sink is best effort.
            }
        }

        _cts.Dispose();
    }
}
