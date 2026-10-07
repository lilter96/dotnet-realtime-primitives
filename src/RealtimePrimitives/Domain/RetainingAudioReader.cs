using System.Threading.Channels;

namespace ShadowPrompter.Core.Domain;

/// <summary>A stretch of retained audio taken out of the live path: 16-bit mono PCM and where it was captured.</summary>
/// <param name="Pcm">The audio, chunk after chunk.</param>
/// <param name="StartTimestampMs">Capture time (the clock of <c>AudioChunk.TimestampMs</c>) at which the first chunk began.</param>
/// <param name="EndTimestampMs">Capture time at which the last chunk ended.</param>
/// <param name="Chunks">How many chunks it holds (0 = nothing was taken).</param>
/// <param name="SampleRate">Samples per second of <paramref name="Pcm"/>.</param>
public readonly record struct AudioBacklog(byte[] Pcm, long StartTimestampMs, long EndTimestampMs, int Chunks, int SampleRate)
{
    public AudioTimeMap? Timeline { get; init; }
    public static AudioBacklog None => new([], 0, 0, 0, 0);
}

/// <summary>
/// The audio between a capture device and a streaming speech-to-text connection that can die at any moment. It drains the capture channel
/// the instant a chunk is produced (the device never waits on the network, and the capture channel never overflows because a socket
/// was reconnecting) and keeps the last <c>capacity</c> chunks, READ OR NOT. The consumer reads from a cursor; when its connection breaks
/// <see cref="RewindTo"/> moves the cursor back to the first chunk the dead connection never got a transcript for, so the next connection
/// starts exactly where the last one failed instead of a minute of the interview going missing. Only what falls out of the retention
/// window UNREAD is lost, and that is reported once (<c>onLoss</c>) like any other audio loss.
/// <para>
/// One consumer at a time (the supervisor runs one connection per speaker at a time); the producer is the internal pump. Chunks keep their
/// own sequence numbers and capture timestamps, so a replayed chunk is the same chunk it was.
/// </para>
/// </summary>
public sealed class RetainingAudioReader : ChannelReader<AudioChunk>, IDisposable
{
    private const long DefaultChunkMs = 100;

    private readonly ChannelReader<AudioChunk> _source;
    private readonly AudioChunk[] _ring;
    private readonly string _sourceKey;
    private readonly Action<GapMarker>? _onLoss;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;

    private long _head;    // absolute index of the oldest retained chunk
    private long _tail;    // absolute index the next chunk is written at
    private long _cursor;  // absolute index the next read returns
    private long _backfillEnd; // absolute index of the first chunk NOT yet handed to a backfill: what is behind it is being transcribed elsewhere
    private TaskCompletionSource _signal = NewSignal();
    private bool _sourceEnded;
    private long _lastReadTimestampMs;
    private long _lostFirstSeq = -1;
    private long _lostLastSeq = -1;
    private long _lostChunks;
    private long _lostMs;

    /// <param name="source">The capture channel (wrapped in whatever observes it: gap detection, an activity meter).</param>
    /// <param name="capacity">How many chunks are retained, read or not (retention seconds x chunks per second).</param>
    /// <param name="onLoss">Told once when chunks fell out of the retention window before they were read.</param>
    public RetainingAudioReader(ChannelReader<AudioChunk> source, int capacity, string sourceKey, Action<GapMarker>? onLoss = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _source = source;
        _ring = new AudioChunk[capacity];
        _sourceKey = sourceKey;
        _onLoss = onLoss;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>The capture source this reader holds audio for ("interviewer" / "candidate").</summary>
    public string Label => _sourceKey;

    public override Task Completion => _completion.Task;

    /// <summary>The capture timestamp of the chunk handed out last (0 before the first).</summary>
    public long LastReadTimestampMs
    {
        get
        {
            lock (_lock)
            {
                return _lastReadTimestampMs;
            }
        }
    }

    /// <summary>The capture start of the oldest chunk not yet handed to the connection (null = it has everything that was captured).</summary>
    public long? FirstUnreadCaptureMs
    {
        get
        {
            lock (_lock)
            {
                return _cursor < _tail ? _ring[(int)(_cursor % _ring.Length)].CaptureStartMs : null;
            }
        }
    }

    /// <summary>The capture end of the newest chunk taken from the source (0 before the first).</summary>
    public long LastCapturedEndMs
    {
        get
        {
            lock (_lock)
            {
                return _tail > _head ? _ring[(int)((_tail - 1) % _ring.Length)].TimestampMs : 0;
            }
        }
    }

    /// <summary>How many chunks the pump has taken from the source in all (diagnostics).</summary>
    public long Produced
    {
        get
        {
            lock (_lock)
            {
                return _tail;
            }
        }
    }

    /// <summary>How many chunks are held right now (read and unread).</summary>
    public int Retained
    {
        get
        {
            lock (_lock)
            {
                return (int)(_tail - _head);
            }
        }
    }

    /// <summary>How many chunks wait to be read.</summary>
    public int Unread
    {
        get
        {
            lock (_lock)
            {
                return (int)(_tail - _cursor);
            }
        }
    }

    public override bool TryRead(out AudioChunk item)
    {
        GapMarker? loss;
        lock (_lock)
        {
            if (_cursor >= _tail)
            {
                item = default;
                return false;
            }

            item = _ring[(int)(_cursor % _ring.Length)];
            _cursor++;
            _lastReadTimestampMs = item.TimestampMs;
            loss = TakeLossLocked(item.TimestampMs);
            if (_sourceEnded && _cursor >= _tail) _completion.TrySetResult();
        }

        Report(loss);
        return true;
    }

    public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_cursor < _tail) return true;
                if (_sourceEnded) return false;
                signal = _signal.Task;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the audio the live connection has no transcript for OUT of its path: everything from the first retained chunk captured after
    /// <paramref name="capturedUpToMs"/> up to the newest <paramref name="liveOverlapChunks"/> chunks, and moves the read cursor there. The live
    /// connection then continues from (almost) NOW instead of working through the backlog first — a streaming provider transcribes about 1.3x
    /// real time however fast the audio is sent, so a backlog read on the live connection delays every later word by its whole length. What was
    /// taken is the caller's to transcribe separately (a batch call, an order of magnitude faster than real time). Nothing is taken (and the
    /// cursor stays) when it would be fewer than <paramref name="minChunks"/> chunks: a short gap is cheaper to replay on the live connection.
    /// </summary>
    public AudioBacklog TakeBacklog(long capturedUpToMs, int liveOverlapChunks, int minChunks)
    {
        lock (_lock)
        {
            var start = _cursor;
            while (start > _head && _ring[(int)((start - 1) % _ring.Length)].TimestampMs > capturedUpToMs) start--;
            // Never again what an earlier take already handed over: the connection that follows a failed connect has no new transcript, so
            // "everything after the last transcript" still begins before that take. A replay through the real network blip of a clean run
            // transcribed the same 20 seconds three times and put every sentence into the dialogue three times.
            start = Math.Max(start, _backfillEnd);
            var end = Math.Max(start, _tail - Math.Max(0, liveOverlapChunks));
            var count = (int)(end - start);
            if (count < Math.Max(1, minChunks)) return AudioBacklog.None;

            var first = _ring[(int)(start % _ring.Length)];
            var last = _ring[(int)((end - 1) % _ring.Length)];
            var pcm = new byte[first.Data.Length * count];
            var offset = 0;
            var timeline = new AudioTimeMap();
            for (var i = start; i < end; i++)
            {
                var chunk = _ring[(int)(i % _ring.Length)];
                var data = chunk.Data;
                timeline.Add(chunk);
                if (offset + data.Length > pcm.Length) Array.Resize(ref pcm, offset + data.Length);
                data.CopyTo(pcm.AsMemory(offset));
                offset += data.Length;
            }

            if (offset != pcm.Length) Array.Resize(ref pcm, offset);
            _cursor = end;
            _backfillEnd = end;
            Wake();
            return new AudioBacklog(pcm, first.CaptureStartMs, last.TimestampMs, count, first.SampleRate) { Timeline = timeline };
        }
    }

    /// <summary>
    /// Moves the read cursor back so the next read returns the first retained chunk whose capture time is AFTER
    /// <paramref name="capturedUpToMs"/> (the end of the audio that is already transcribed). The cursor never moves forward, and cannot go
    /// behind what is retained. Returns how many chunks will be read again.
    /// </summary>
    public int RewindTo(long capturedUpToMs)
    {
        lock (_lock)
        {
            var target = _cursor;
            while (target > _head && _ring[(int)((target - 1) % _ring.Length)].TimestampMs > capturedUpToMs) target--;
            // Audio that went to a backfill is not replayed on the live connection as well (the same words would arrive twice).
            target = Math.Min(_cursor, Math.Max(target, _backfillEnd));
            var replayed = (int)(_cursor - target);
            _cursor = target;
            if (replayed > 0) Wake();
            return replayed;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The pump ends by cancellation
        }

        _cts.Dispose();
        _completion.TrySetResult();
        lock (_lock)
        {
            _sourceEnded = true;
            Wake();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var chunk in _source.ReadAllAsync(_cts.Token).ConfigureAwait(false)) Append(chunk);
        }
        catch (OperationCanceledException)
        {
            // Disposed
        }
        catch
        {
            // A source that faults ends like one that completes: the consumer drains what is held, then sees the end
        }
        finally
        {
            lock (_lock)
            {
                _sourceEnded = true;
                if (_cursor >= _tail) _completion.TrySetResult();
                Wake();
            }
        }
    }

    private void Append(AudioChunk chunk)
    {
        lock (_lock)
        {
            if (_tail - _head == _ring.Length)
            {
                // Full: the slot about to be overwritten holds the oldest chunk. If nobody read it, it is audio the transcriber never hears.
                var evicted = _ring[(int)(_head % _ring.Length)];
                if (_cursor == _head)
                {
                    NoteLossLocked(evicted);
                    _cursor++;
                }

                _head++;
            }

            _ring[(int)(_tail % _ring.Length)] = chunk;
            _tail++;
            Wake();
        }
    }

    private void NoteLossLocked(AudioChunk lost)
    {
        if (_lostChunks == 0) _lostFirstSeq = lost.Sequence;
        _lostLastSeq = lost.Sequence;
        _lostChunks++;
        _lostMs += ChunkMs(lost);
    }

    private GapMarker? TakeLossLocked(long captureMs)
    {
        if (_lostChunks == 0) return null;
        var marker = new GapMarker
        {
            SourceId = _sourceKey,
            FirstMissingSequence = _lostFirstSeq,
            LastMissingSequence = _lostLastSeq,
            DurationMs = _lostMs,
            TimestampUtc = DateTime.UtcNow,
            CaptureMs = captureMs
        };
        _lostChunks = 0;
        _lostMs = 0;
        _lostFirstSeq = _lostLastSeq = -1;
        return marker;
    }

    private void Report(GapMarker? loss)
    {
        if (loss is null || _onLoss is null) return;
        try
        {
            _onLoss(loss);
        }
        catch
        {
            // An observer must never break audio ingestion
        }
    }

    private static long ChunkMs(AudioChunk chunk)
    {
        var bytesPerSecond = (long)chunk.SampleRate * chunk.Channels * 2;
        return bytesPerSecond > 0 && chunk.Data.Length > 0 ? chunk.Data.Length * 1000L / bytesPerSecond : DefaultChunkMs;
    }

    // Under the lock.
    private void Wake()
    {
        var old = _signal;
        _signal = NewSignal();
        old.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
