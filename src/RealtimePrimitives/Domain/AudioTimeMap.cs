namespace ShadowPrompter.Core.Domain;

/// <summary>Offsets in the audio actually sent, mapped to capture time. Pauses, skips and device epochs are not audio samples.</summary>
public sealed class AudioTimeMap(int capacity = 36_000)
{
    private readonly Lock _lock = new();
    private readonly List<Span> _spans = [];
    private double _duration;
    private int _first;

    private readonly record struct Span(double From, double To, double CaptureFrom, double CaptureTo, long FirstSeq, long LastSeq);

    public double DurationSeconds { get { lock (_lock) return _duration; } }

    public void Add(AudioChunk chunk) => Add(chunk.Data.Length / (chunk.SampleRate * chunk.Channels * 2.0),
        chunk.CaptureStartMs, chunk.TimestampMs, chunk.Sequence, chunk.Sequence);

    public void Add(double seconds, double startMs, double endMs, long firstSeq = 0, long lastSeq = 0)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || !double.IsFinite(startMs) || !double.IsFinite(endMs) || endMs < startMs)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (_lock)
        {
            _spans.Add(new Span(_duration, _duration + seconds, startMs, endMs, firstSeq, lastSeq));
            _duration += seconds;
            if (_spans.Count - _first > capacity) _first++;
            if (_first >= capacity) { _spans.RemoveRange(0, _first); _first = 0; }
        }
    }

    /// <param name="end">At a boundary an end belongs to the preceding span; a start belongs to the following span.</param>
    public (long CaptureMs, long Sequence) At(double seconds, bool end = false)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (_lock)
        {
            if (_spans.Count == _first) return default;
            var lo = _first;
            var hi = _spans.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (seconds > _spans[mid].To + 1e-8 || !end && seconds >= _spans[mid].To - 1e-8) lo = mid + 1;
                else hi = mid;
            }
            var s = _spans[lo];
            // Outside retained evidence only a nominal extrapolation is possible (legacy providers/tests); no arrival clock is used.
            var fraction = (seconds - s.From) / (s.To - s.From);
            var capture = s.CaptureFrom + fraction * (s.CaptureTo - s.CaptureFrom);
            return ((long)Math.Round(capture), end ? s.LastSeq : s.FirstSeq);
        }
    }
}
