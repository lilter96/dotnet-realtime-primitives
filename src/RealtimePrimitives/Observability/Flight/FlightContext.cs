namespace ShadowPrompter.Core.Observability.Flight;

/// <summary>
/// What an event belongs to, carried by the async flow instead of by parameters: an answer run enters its generation here, and every event
/// recorded anywhere below it — the pool's failover loop, a hedged attempt on another thread, the HTTP client, the stream reader — comes out
/// tagged with that generation, without one of them having been told which answer it works for. This is what lets a post-mortem line up
/// "attempt 2 failed" with "the pane was cleared" with "the second press".
/// </summary>
/// <param name="Pipeline">Which pipeline owns the run: <c>voice</c>, <c>vision</c>, <c>summary</c>…</param>
/// <param name="Generation">The run's generation on that pipeline (voice and vision count separately, hence both).</param>
/// <param name="Target">The pool target (<c>provider/model#key</c>) an attempt runs on, once there is one: a hedged race has several at a time.</param>
public sealed record FlightContext(string Pipeline, long Generation, string? Target = null)
{
    private static readonly AsyncLocal<FlightContext?> Slot = new();

    public static FlightContext? Current => Slot.Value;

    /// <summary>Makes the calling flow (and everything it starts) belong to this run; dispose to leave it.</summary>
    public static IDisposable Enter(string pipeline, long generation)
    {
        var previous = Slot.Value;
        Slot.Value = new FlightContext(pipeline, generation);
        return new Leave(previous);
    }

    /// <summary>Names the target of the attempt the calling flow runs; keeps the run it belongs to.</summary>
    public static IDisposable EnterTarget(string target)
    {
        var previous = Slot.Value;
        Slot.Value = (previous ?? new FlightContext("none", 0)) with { Target = target };
        return new Leave(previous);
    }

    private sealed class Leave(FlightContext? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }
}
