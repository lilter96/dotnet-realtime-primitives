using System.Runtime.InteropServices;

namespace ShadowPrompter.Core.Domain;

/// <summary>What one capture source sounded like over an interval.</summary>
/// <param name="Chunks">100 ms chunks measured.</param>
/// <param name="RmsDbfs">Average power in dBFS over the interval (-120 = digital silence).</param>
/// <param name="PeakDbfs">The loudest sample, dBFS.</param>
/// <param name="ActiveRatio">Share of chunks loud enough to count as speech (the same threshold the speech meter uses).</param>
/// <param name="ZeroRatio">Share of chunks that were digital zeros (a muted or dead source, not a quiet room).</param>
/// <param name="Clipped">Chunks with a sample at the rail (a gain that is too high: speech recognition suffers).</param>
public readonly record struct AudioLevels(int Chunks, double RmsDbfs, double PeakDbfs, double ActiveRatio, double ZeroRatio, int Clipped);

/// <summary>
/// Accumulates the levels of 16-bit PCM chunks and hands them out per interval. The two things it exists to answer after an interview: "was the
/// interviewer's channel silent when the speech provider heard nothing?" (a wrong device, a muted source) and "was the signal clipping or
/// buried in noise?". Written by one reader thread; <see cref="TryTake"/> is called from that same thread, so no locking.
/// </summary>
public sealed class AudioLevelMeter(int activeRmsThreshold = SpeechActivity.DefaultRmsThreshold)
{
    private int _chunks;
    private double _sumSquares;
    private long _samples;
    private int _peak;
    private int _active;
    private int _zero;
    private int _clipped;

    public void Observe(ReadOnlySpan<byte> pcm16)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm16[..(pcm16.Length & ~1)]);
        if (samples.IsEmpty) return;

        long sumSq = 0;
        var peak = 0;
        foreach (var s in samples)
        {
            sumSq += (long)s * s;
            var a = s == short.MinValue ? short.MaxValue : Math.Abs((int)s);
            if (a > peak) peak = a;
        }

        _chunks++;
        _sumSquares += sumSq;
        _samples += samples.Length;
        if (peak > _peak) _peak = peak;
        if (sumSq == 0) _zero++;
        if (peak >= 32700) _clipped++;
        if (sumSq >= (long)activeRmsThreshold * activeRmsThreshold * samples.Length) _active++;
    }

    /// <summary>The levels since the last take (and starts a new interval); false when no chunk arrived.</summary>
    public bool TryTake(out AudioLevels levels)
    {
        if (_chunks == 0)
        {
            levels = default;
            return false;
        }

        var rms = Math.Sqrt(_sumSquares / Math.Max(1, _samples));
        levels = new AudioLevels(_chunks, ToDbfs(rms), ToDbfs(_peak), _active / (double)_chunks, _zero / (double)_chunks, _clipped);
        _chunks = 0;
        _sumSquares = 0;
        _samples = 0;
        _peak = 0;
        _active = 0;
        _zero = 0;
        _clipped = 0;
        return true;
    }

    private static double ToDbfs(double amplitude) => amplitude < 1 ? -120 : Math.Max(-120, 20 * Math.Log10(amplitude / 32768.0));
}
