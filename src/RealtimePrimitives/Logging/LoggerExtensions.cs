
namespace ShadowPrompter.Core.Logging;

/// <summary>
/// Pre-formatted log lines. The application's lines are already complete text (<c>[pool] model X rests 30s</c>), so they bypass the
/// message-template parser: a brace inside a transcript, a JSON body or a prompt can never be taken for a placeholder.
/// </summary>
public static class LoggerExtensions
{
    public static void Info(this ILogger log, string message) => Write(log, LogLevel.Information, message, null);

    public static void Warn(this ILogger log, string message, Exception? exception = null) => Write(log, LogLevel.Warning, message, exception);

    public static void Error(this ILogger log, string message, Exception? exception = null) => Write(log, LogLevel.Error, message, exception);

    public static void Debug(this ILogger log, string message) => Write(log, LogLevel.Debug, message, null);

    private static void Write(ILogger log, LogLevel level, string message, Exception? exception)
    {
        if (!log.IsEnabled(level)) return;
        log.Log(level, default, message, exception, static (m, _) => m);
    }
}
