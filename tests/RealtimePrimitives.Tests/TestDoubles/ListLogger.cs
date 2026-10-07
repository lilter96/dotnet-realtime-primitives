
namespace ShadowPrompter.Tests.TestDoubles;

/// <summary>Collects formatted log lines (thread-safe) so a test can assert on what a component reported.</summary>
public sealed class ListLogger(List<string>? sink = null) : ILogger
{
    private readonly Lock _lock = new();

    public List<string> Lines { get; } = sink ?? [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lock) Lines.Add(formatter(state, exception));
    }
}
