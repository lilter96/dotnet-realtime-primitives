namespace ShadowPrompter.Core.Domain;

public enum Speaker
{
    Interviewer = 0,
    Candidate = 1
}

/// <summary>The text identity of a speaker's capture source: chunk <c>SourceId</c>, gap keys, STT labels and log lines all use these.</summary>
public static class SpeakerExtensions
{
    public const string InterviewerId = "interviewer";
    public const string CandidateId = "candidate";

    public static string SourceId(this Speaker speaker) => speaker == Speaker.Interviewer ? InterviewerId : CandidateId;
}
