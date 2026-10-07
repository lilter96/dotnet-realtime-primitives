using System.Threading.Channels;

namespace ShadowPrompter.Core.Domain;

/// <summary>
///     Pass-through <see cref="ChannelReader{T}" /> that watches the chunk sequence of
///     one audio source and reports real audio loss (sequence jump from DropOldest
///     overflow, or a capture-clock hole from a device reopen) via a callback.
///     Wrapping the reader keeps loss detection provider-agnostic: Groq REST, Grok and
///     Deepgram all consume the same wrapped stream. Losses shorter than
///     <c>minGapMs</c> are ignored so the LLM prompt never carries noise markers.
/// </summary>
public sealed class GapDetectingReader : ChannelReader<AudioChunk>
{
    public const long DefaultMinGapMs = 1000;

    private readonly ChannelReader<AudioChunk> _inner;
    private readonly string _sourceKey;
    private readonly Action<GapMarker> _onGap;
    private readonly long _minGapMs;
    private readonly Action<AudioChunk>? _onChunk;
    private readonly TurnSequencer _sequencer = new();

    /// <param name="onChunk">Optional tap on every chunk that passes (e.g. a speech-activity meter); errors are swallowed.</param>
    public GapDetectingReader(
        ChannelReader<AudioChunk> inner, string sourceKey, Action<GapMarker> onGap, long minGapMs = DefaultMinGapMs,
        Action<AudioChunk>? onChunk = null)
    {
        _inner = inner;
        _sourceKey = sourceKey;
        _onGap = onGap;
        _minGapMs = minGapMs;
        _onChunk = onChunk;
    }

    public override Task Completion => _inner.Completion;

    public override bool TryRead(out AudioChunk item)
    {
        if (!_inner.TryRead(out item)) return false;
        Observe(in item);
        return true;
    }

    public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
        _inner.WaitToReadAsync(cancellationToken);

    private void Observe(in AudioChunk chunk)
    {
        if (_onChunk is not null)
        {
            try
            {
                _onChunk(chunk);
            }
            catch
            {
                // A meter must never break audio ingestion
            }
        }

        if (chunk.Sequence <= 0) return; // producer without sequence numbers: nothing to verify

        var gap = _sequencer.ObserveChunk(_sourceKey, chunk.Sequence, chunk.TimestampMs,
            chunk.TimestampMs - chunk.CaptureStartMs, chunk.KnownIdleBefore ? long.MaxValue : 2000);
        if (gap is null || gap.DurationMs < _minGapMs) return;

        try
        {
            _onGap(gap);
        }
        catch
        {
            // Observer errors must never break audio ingestion
        }
    }
}
