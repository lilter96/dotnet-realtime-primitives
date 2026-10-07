using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ShadowPrompter.Core.Logging;

/// <summary>
/// The last line of defence for credentials in diagnostics. Nothing records a key on purpose, but a provider may quote one back in an error
/// body ("invalid key sk-…; Authorization: Bearer sk-…"), and that body used to reach the log line and the flight event verbatim. Every log line
/// (<c>LineLoggerProvider</c>, and through it the Loki bridge) and every recorded failure message passes through <see cref="Redact"/>: the values
/// the configuration holds as keys are replaced, and so is anything shaped like a credential (a Bearer / Token value, an <c>sk-</c> / <c>gsk_</c> key).
/// </summary>
public static partial class Secrets
{
    public const string Mask = "***";

    private static readonly ConcurrentDictionary<string, byte> Known = new(StringComparer.Ordinal);

    /// <summary>A value to hide wherever it appears (shorter than 8 characters is not a credential and would mask ordinary words).</summary>
    public static void Register(string? value)
    {
        var trimmed = value?.Trim();
        if (trimmed is { Length: >= 8 }) Known.TryAdd(trimmed, 0);
    }

    /// <summary>Whether a configuration path names a credential: a segment <c>ApiKey</c> / <c>Key</c> / <c>Keys</c>, or one that speaks of a token,
    /// secret, password or connection string (<c>Hotkeys</c> is not one).</summary>
    public static bool IsSecretPath(string path) => path.Split(':').Any(segment => SecretSegment().IsMatch(segment));

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var secret in Known.Keys)
            if (text.Contains(secret, StringComparison.Ordinal)) text = text.Replace(secret, Mask, StringComparison.Ordinal);
        text = SchemeValue().Replace(text, m => m.Groups[1].Value + " " + Mask);
        return KeyShaped().Replace(text, Mask);
    }

    [GeneratedRegex(@"^(?:api)?keys?$|token|secret|password|^connectionstring$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretSegment();

    // An HTTP auth scheme followed by a credential-like value (it has a digit: "first token received" is prose, not a credential).
    [GeneratedRegex(@"\b(Bearer|Token|Basic)\s+(?=[A-Za-z._~+/=\-]*\d)[A-Za-z0-9._~+/=\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeValue();

    [GeneratedRegex(@"\b(?:sk|gsk|xai|pk|rk)[-_][A-Za-z0-9_\-]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyShaped();
}
