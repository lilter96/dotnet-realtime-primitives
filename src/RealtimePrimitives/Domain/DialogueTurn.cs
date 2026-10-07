namespace ShadowPrompter.Core.Domain;

public sealed class DialogueTurn
{
    private static long s_nextTurnId;

    public long TurnId { get; set; }
    /// <summary>Identity shared by snapshots and the final of ONE streaming utterance; 0 for legacy/REST turns.</summary>
    public long TranscriptId { get; set; }
    public long TranscriptRevision { get; set; }
    public Speaker Speaker { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Capture window of the underlying audio (authoritative ordering).</summary>
    public long CaptureStartMs { get; set; }

    public long CaptureEndMs { get; set; }

    /// <summary>Chunk sequence range this turn was built from (gap detection).</summary>
    public long FirstSeq { get; set; }

    public long LastSeq { get; set; }

    /// <summary>
    ///     Estimated speech duration in ms (REST: counted speech chunks; WS: rough
    ///     text-length estimate). Used ONLY by the noise filter — never by ASR logic.
    /// </summary>
    public long AudioMs { get; set; }

    /// <summary>
    ///     True for a turn recovered AFTER the fact: speech from an outage that the speech provider's batch endpoint transcribed once the
    ///     connection was back. It belongs in the history (ordered by its capture time) but it is old news: it must not wake the auto-answer for
    ///     a question that was asked half a minute ago.
    /// </summary>
    public bool IsBackfill { get; set; }

    /// <summary>
    ///     The words of the turn with their capture times, when the provider says where each word is (Deepgram). What a press may take from a
    ///     turn that was still being spoken when it came: only the words said before it (<c>QuestionSnapshot</c>). Null = no word timing.
    /// </summary>
    public IReadOnlyList<TimedWord>? Words { get; set; }

    /// <summary>Global monotonic process id: orders conversation, never completion.</summary>
    public static long NextTurnId()
    {
        return Interlocked.Increment(ref s_nextTurnId);
    }
}

/// <summary>
///     Observable audio loss: a chunk-sequence gap on a source. Rendered into the
///     dialogue context so the LLM never confidently bridges an unknown gap.
/// </summary>
public sealed class GapMarker
{
    public string SourceId { get; set; } = string.Empty;
    public long FirstMissingSequence { get; set; }
    public long LastMissingSequence { get; set; }
    public long DurationMs { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Monotonic capture clock (Environment.TickCount64 domain) at which the loss
    ///     was observed; orders the marker against turns by capture time. 0 = unknown.
    /// </summary>
    public long CaptureMs { get; set; }
}

/// <summary>One recognised word on the capture clock (the clock of <c>AudioChunk.TimestampMs</c>).</summary>
public readonly record struct TimedWord(string Text, long StartMs, long EndMs);
