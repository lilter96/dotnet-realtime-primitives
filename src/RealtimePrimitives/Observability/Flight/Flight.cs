namespace ShadowPrompter.Core.Observability.Flight;

/// <summary>
/// Where any layer reaches the session's flight recorder. Telemetry is the ONE ambient dependency of this code base — exactly like
/// <see cref="AppMetrics.Meter"/>: threading a recorder through twenty constructors would put an observer into every signature for
/// something that must be invisible to the logic it watches. A process that installed nothing records nothing, for free. Tests scope their
/// own recorder with <see cref="Scope"/> (flows with the async context), so parallel tests never see each other's events.
/// </summary>
public static class Flight
{
    private static IFlightRecorder _installed = NullFlightRecorder.Instance;
    private static readonly AsyncLocal<IFlightRecorder?> Scoped = new();

    /// <summary>The recorder in effect for the calling flow: a scoped one (tests), else the process-wide one, else the null recorder.</summary>
    public static IFlightRecorder Recorder => Scoped.Value ?? Volatile.Read(ref _installed);

    public static bool On => Recorder.Enabled;

    /// <summary>Starts an event: <c>Flight.Event("net.status").Str("to", "offline").Emit()</c>.</summary>
    public static FlightEvent Event(string kind) => Recorder.Begin(kind);

    /// <summary>Makes <paramref name="recorder"/> the process-wide recorder (the composition root of the live session); returns what it replaced.</summary>
    public static IFlightRecorder Install(IFlightRecorder recorder) => Interlocked.Exchange(ref _installed, recorder);

    /// <summary>Removes <paramref name="recorder"/> if it is still the installed one (a later session may already have replaced it).</summary>
    public static void Uninstall(IFlightRecorder recorder) =>
        Interlocked.CompareExchange(ref _installed, NullFlightRecorder.Instance, recorder);

    /// <summary>Scopes a recorder to the current async flow; dispose to restore the previous one.</summary>
    public static IDisposable Scope(IFlightRecorder recorder)
    {
        var previous = Scoped.Value;
        Scoped.Value = recorder;
        return new Restore(previous);
    }

    private sealed class Restore(IFlightRecorder? previous) : IDisposable
    {
        public void Dispose() => Scoped.Value = previous;
    }
}
