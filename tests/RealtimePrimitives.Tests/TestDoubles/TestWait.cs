using System.Threading.Channels;
using Xunit;

namespace ShadowPrompter.Tests.TestDoubles;

/// <summary>
/// Bounded waits that FAIL a test. A timeout built from a <see cref="CancellationTokenSource"/> ends in an
/// <see cref="OperationCanceledException"/>, and xUnit v3 (1.0.1) counts that as a pass, so a hang would turn green; the wait here
/// throws <see cref="TimeoutException"/> instead. The runner's own token rides along so an aborted run stops promptly.
/// </summary>
internal static class TestWait
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    public static Task Within(this Task task) => task.WaitAsync(Deadline, TestContext.Current.CancellationToken);

    public static Task<T> Within<T>(this Task<T> task) => task.WaitAsync(Deadline, TestContext.Current.CancellationToken);

    /// <summary>Polls a condition until it holds; fails the test (a <see cref="TimeoutException"/>, never a cancellation) when it does not.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? within = null)
    {
        var until = DateTime.UtcNow + (within ?? Deadline);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"still waiting for: {what}");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    public static Task<T> ReadWithin<T>(this ChannelReader<T> reader) =>
        reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().Within();

    /// <summary>
    /// Reads whatever is buffered until the writer is done. A reader's <c>Completion</c> only completes once the buffered items are
    /// consumed too, so "the engine stopped" is asserted by draining, not by awaiting <c>Completion</c> alone.
    /// </summary>
    public static async Task DrainAsync<T>(this ChannelReader<T> reader)
    {
        while (await reader.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask().Within())
            while (reader.TryRead(out _))
            {
            }
    }
}
