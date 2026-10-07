namespace ShadowPrompter.Core.Domain;

/// <summary>
///     A single PCM chunk (default 100ms @ 16kHz S16LE mono = 3200 bytes).
///     Owns its memory; producers copy pooled buffers into <see cref="Data" />.
///     Identity contract (Batch 1): <see cref="SourceId" /> + <see cref="Sequence" />
///     order capture; network completion order NEVER defines conversation order.
/// </summary>
public readonly struct AudioChunk
{
    public ReadOnlyMemory<byte> Data { get; }
    public Speaker Speaker { get; }
    public long TimestampMs { get; }
    public long CaptureStartMs { get; }
    public long CaptureEpoch { get; }
    public bool TimestampEstimated { get; }
    public bool KnownIdleBefore { get; }
    public long SampleCount { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Stable capture-source id ("interviewer"/"candidate").</summary>
    public string SourceId { get; }

    /// <summary>Monotonic sequence within the source (gap detection).</summary>
    public long Sequence { get; }

    public AudioChunk(ReadOnlyMemory<byte> data, Speaker speaker, long timestampMs, long sampleCount = 0,
        int sampleRate = 16000, int channels = 1, string sourceId = "", long sequence = 0,
        long? captureStartMs = null, long captureEpoch = 0, bool timestampEstimated = false, bool knownIdleBefore = false)
    {
        Data = data;
        Speaker = speaker;
        TimestampMs = timestampMs;
        CaptureStartMs = captureStartMs ?? timestampMs - (sampleRate > 0 && channels > 0 ? data.Length * 1000L / (sampleRate * channels * 2L) : 0);
        CaptureEpoch = captureEpoch;
        TimestampEstimated = timestampEstimated;
        KnownIdleBefore = knownIdleBefore;
        SampleCount = sampleCount;
        SampleRate = sampleRate;
        Channels = channels;
        SourceId = sourceId;
        Sequence = sequence;
    }
}
