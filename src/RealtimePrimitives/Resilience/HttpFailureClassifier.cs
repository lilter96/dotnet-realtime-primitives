using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;

namespace ShadowPrompter.Core.Resilience;

/// <summary>
/// What an HTTP (or WebSocket handshake) failure means, decided once: is our credential refused, and how long does the server want us to
/// stay away. Transport clients, the answer pool, the quota sync and the STT supervisor ask here instead of each re-reading status codes and
/// headers with its own copy of the rule.
/// </summary>
public static class HttpFailureClassifier
{
    /// <summary>401 / 403: the server refused the credential. Retrying the same request cannot help.</summary>
    public static bool IsAuthRejection(HttpStatusCode? status) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>
    /// True when the exception, or anything it wraps, is an auth rejection: an HTTP 401/403, or a WebSocket handshake refused with one
    /// (the handshake exposes the status only as text in the message).
    /// </summary>
    public static bool IsAuthFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException http && IsAuthRejection(http.StatusCode)) return true;
            if (e is WebSocketException && MentionsAuthRejection(e.Message)) return true;
        }

        return false;
    }

    /// <summary>
    /// When the server asks to be tried again: <c>retry-after-ms</c> (the precise, OpenAI-style header) first, then
    /// <c>Retry-After</c> as delta-seconds or an HTTP date. Null when there is no usable header; never negative. Not clamped: every caller
    /// has its own budget for how long it is willing to wait.
    /// </summary>
    public static TimeSpan? RetryAfter(HttpResponseHeaders headers, DateTimeOffset? now = null)
    {
        if (FirstValue(headers, "retry-after-ms") is { } ms
            && long.TryParse(ms.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis) && millis >= 0)
            return TimeSpan.FromMilliseconds(millis);

        if (FirstValue(headers, "Retry-After") is not { } raw) return null;

        raw = raw.Trim();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var left = date - (now ?? DateTimeOffset.UtcNow);
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        return null;
    }

    private static bool MentionsAuthRejection(string message) =>
        message.Contains("401", StringComparison.Ordinal) || message.Contains("403", StringComparison.Ordinal)
        || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase);

    private static string? FirstValue(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        foreach (var value in values) return value;
        return null;
    }
}
