using System.Runtime.InteropServices;

namespace ShadowPrompter.Core.Domain;

/// <summary>
///     Provider-independent "is someone speaking right now" for one audio source: the last time a chunk was loud enough
///     (RMS at or above the threshold), with a hangover so the short pauses inside a sentence do not read as silence.
///     Auto-answer holds while the interviewer is mid-sentence; before this only the Groq REST VAD had that signal, so with
///     the WebSocket providers (Deepgram, Grok) a long question was answered on its first chunk. Thread-safe: written by the
///     reader thread, read by the scheduler.
/// </summary>
public sealed class SpeechActivity
{
    /// <summary>Same energy threshold the Groq VAD uses for speech (16-bit PCM RMS).</summary>
    public const int DefaultRmsThreshold = 300;

    private readonly Func<long> _nowMs;
    private readonly int _rmsThreshold;
    private long _lastLoudMs = long.MinValue / 4;

    public SpeechActivity(Func<long>? nowMs = null, int rmsThreshold = DefaultRmsThreshold)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _rmsThreshold = rmsThreshold;
    }

    /// <summary>Feeds one chunk of 16-bit little-endian mono PCM.</summary>
    public void Observe(ReadOnlySpan<byte> pcm16)
    {
        if (!IsLoud(pcm16, _rmsThreshold)) return;
        Volatile.Write(ref _lastLoudMs, _nowMs());
    }

    /// <summary>True while speech was heard within the last <paramref name="hangoverMs"/>.</summary>
    public bool IsActive(int hangoverMs) => _nowMs() - Volatile.Read(ref _lastLoudMs) <= hangoverMs;

    /// <summary>RMS of 16-bit little-endian mono PCM at or above <paramref name="threshold"/>; the one energy test the meter and the Groq VAD share.</summary>
    public static bool IsLoud(ReadOnlySpan<byte> pcm16, int threshold)
    {
        if (pcm16.Length < 2) return false;
        var samples = MemoryMarshal.Cast<byte, short>(pcm16[..(pcm16.Length & ~1)]);
        if (samples.IsEmpty) return false;

        long sumSq = 0;
        foreach (var s in samples) sumSq += (long)s * s;

        // RMS >= threshold  <=>  sumSq >= threshold^2 * N
        return sumSq >= (long)threshold * threshold * samples.Length;
    }
}
