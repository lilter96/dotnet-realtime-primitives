using Microsoft.Extensions.Time.Testing;

namespace ShadowPrompter.Tests.TestDoubles;

/// <summary>Test clock: starts at an explicit instant, advanced manually (<see cref="FakeTimeProvider.Advance"/>).</summary>
public sealed class ManualPoolClock(DateTime startUtc)
    : FakeTimeProvider(new DateTimeOffset(startUtc.Kind == DateTimeKind.Utc ? startUtc : startUtc.ToUniversalTime()))
{
    public DateTime UtcNow => GetUtcNow().UtcDateTime;
}
