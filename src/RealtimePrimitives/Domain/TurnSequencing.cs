namespace ShadowPrompter.Core.Domain;

/// <summary>
///     Gap detector over per-source chunk sequences. First observation per source
///     seeds expectations (no gap); every later observation either continues or opens
///     an observable <see cref="GapMarker" />. Pure logic, deterministic, thread-safe.
///     Feed it PER CHUNK (<see cref="ObserveChunk" />, at the point where the audio
///     channel is read): only there does a sequence jump mean real loss (DropOldest
///     overflow, device reopen). Turn-level ranges cannot tell silence between
///     utterances from lost audio, so they must not be pushed here.
/// </summary>
public sealed class TurnSequencer
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _expected = new();
    private readonly Dictionary<string, long> _lastTimestampMs = new();

    /// <summary>Range form (tests, tools): a range starting past the expected sequence opens a gap.</summary>
    public GapMarker? Push(string sourceKey, long firstSeq, long lastSeq, long chunkMs = 100)
    {
        if (lastSeq < firstSeq) (firstSeq, lastSeq) = (lastSeq, firstSeq);
        lock (_lock)
        {
            if (!_expected.TryGetValue(sourceKey, out var exp))
            {
                _expected[sourceKey] = lastSeq + 1;
                return null;
            }

            GapMarker? gap = null;
            if (firstSeq > exp)
                gap = new GapMarker
                {
                    SourceId = sourceKey,
                    FirstMissingSequence = exp,
                    LastMissingSequence = firstSeq - 1,
                    DurationMs = (firstSeq - exp) * chunkMs,
                    TimestampUtc = DateTime.UtcNow
                };
            if (lastSeq + 1 > exp) _expected[sourceKey] = lastSeq + 1;
            return gap;
        }
    }

    /// <summary>
    ///     Chunk form: a sequence jump is lost audio (channel overflow). A contiguous
    ///     sequence with a capture-clock hole of at least <paramref name="minTimeHoleMs" />
    ///     is lost audio too (device reopen / suspend: the source produced nothing).
    /// </summary>
    public GapMarker? ObserveChunk(
        string sourceKey, long sequence, long timestampMs, long chunkMs = 100, long minTimeHoleMs = 2000)
    {
        lock (_lock)
        {
            var hadPrev = _expected.TryGetValue(sourceKey, out var exp);
            _lastTimestampMs.TryGetValue(sourceKey, out var lastTs);

            GapMarker? gap = null;
            if (hadPrev)
            {
                if (sequence > exp)
                {
                    gap = new GapMarker
                    {
                        SourceId = sourceKey,
                        FirstMissingSequence = exp,
                        LastMissingSequence = sequence - 1,
                        DurationMs = (sequence - exp) * chunkMs,
                        TimestampUtc = DateTime.UtcNow,
                        CaptureMs = timestampMs
                    };
                }
                else if (lastTs > 0 && timestampMs - lastTs - chunkMs >= minTimeHoleMs)
                {
                    gap = new GapMarker
                    {
                        SourceId = sourceKey,
                        FirstMissingSequence = sequence,
                        LastMissingSequence = sequence,
                        DurationMs = timestampMs - lastTs - chunkMs,
                        TimestampUtc = DateTime.UtcNow,
                        CaptureMs = timestampMs
                    };
                }
            }

            if (!hadPrev || sequence + 1 > exp) _expected[sourceKey] = sequence + 1;
            _lastTimestampMs[sourceKey] = timestampMs;
            return gap;
        }
    }
}
