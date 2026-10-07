using System.Threading.Channels;
using ShadowPrompter.Core.Domain;
using ShadowPrompter.Tests.TestDoubles;
using Xunit;

namespace ShadowPrompter.Tests.Audio;

/// <summary>
/// The audio between the capture device and a connection that can die: drained the instant it is produced, kept read or not, replayable
/// from the point the dead connection stopped getting transcripts. Only what falls out of the window unread is lost, and that is reported.
/// </summary>
public sealed class RetainingAudioReaderTests
{
    private static AudioChunk Chunk(long sequence) =>
        new(new byte[3200], Speaker.Interviewer, timestampMs: sequence * 100, sampleCount: 1600, sourceId: "interviewer", sequence: sequence);

    private static Channel<AudioChunk> Source() => Channel.CreateUnbounded<AudioChunk>();

    private static async Task<List<long>> ReadSequencesAsync(ChannelReader<AudioChunk> reader, int count)
    {
        var sequences = new List<long>();
        while (sequences.Count < count)
        {
            var chunk = await reader.ReadWithin();
            sequences.Add(chunk.Sequence);
        }

        return sequences;
    }

    [Fact]
    public async Task Chunks_Produced_While_Nobody_Reads_Are_Waiting_In_Order()
    {
        var source = Source();
        using var reader = new RetainingAudioReader(source.Reader, capacity: 100, "interviewer");

        for (var i = 1; i <= 30; i++) await source.Writer.WriteAsync(Chunk(i), TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => reader.Unread == 30, "the pump to drain the capture channel");

        Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), await ReadSequencesAsync(reader, 30));
    }

    [Fact]
    public async Task The_Capture_Channel_Is_Drained_Even_When_The_Consumer_Is_Away()
    {
        // The capture channel of the real engine is a small DropOldest ring: if the pump did not drain it, a reconnect would lose audio there.
        var capture = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(5) { FullMode = BoundedChannelFullMode.DropOldest });
        using var reader = new RetainingAudioReader(capture.Reader, capacity: 100, "interviewer");

        for (var i = 1; i <= 40; i++)
        {
            await capture.Writer.WriteAsync(Chunk(i), TestContext.Current.CancellationToken);
            await TestWait.UntilAsync(() => reader.Unread == i, $"chunk {i} to be drained");
        }

        Assert.Equal(Enumerable.Range(1, 40).Select(i => (long)i), await ReadSequencesAsync(reader, 40));
    }

    [Fact]
    public async Task Rewinding_Replays_Everything_After_The_Transcribed_Audio_And_Nothing_Before_It()
    {
        var source = Source();
        using var reader = new RetainingAudioReader(source.Reader, capacity: 100, "interviewer");
        for (var i = 1; i <= 20; i++) await source.Writer.WriteAsync(Chunk(i), TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => reader.Unread == 20, "the pump");
        await ReadSequencesAsync(reader, 15); // the dead connection was sent 15 chunks

        var replayed = reader.RewindTo(capturedUpToMs: 1000); // transcripts exist up to the end of chunk 10 (timestamp 1000)

        Assert.Equal(5, replayed);
        Assert.Equal(Enumerable.Range(11, 10).Select(i => (long)i), await ReadSequencesAsync(reader, 10));
    }

    [Fact]
    public async Task Rewinding_Never_Moves_The_Cursor_Forward()
    {
        var source = Source();
        using var reader = new RetainingAudioReader(source.Reader, capacity: 100, "interviewer");
        for (var i = 1; i <= 10; i++) await source.Writer.WriteAsync(Chunk(i), TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => reader.Unread == 10, "the pump");
        await ReadSequencesAsync(reader, 4);

        Assert.Equal(0, reader.RewindTo(capturedUpToMs: long.MaxValue)); // everything is transcribed: nothing to replay

        Assert.Equal(Enumerable.Range(5, 6).Select(i => (long)i), await ReadSequencesAsync(reader, 6));
    }

    [Fact]
    public async Task A_Rewind_Cannot_Go_Behind_What_Is_Still_Retained()
    {
        var source = Source();
        using var reader = new RetainingAudioReader(source.Reader, capacity: 10, "interviewer");
        for (var i = 1; i <= 25; i++) await source.Writer.WriteAsync(Chunk(i), TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => reader.Produced == 25, "the pump");
        await ReadSequencesAsync(reader, 10); // 16..25

        var replayed = reader.RewindTo(capturedUpToMs: 0);

        Assert.Equal(10, replayed);
        Assert.Equal(Enumerable.Range(16, 10).Select(i => (long)i), await ReadSequencesAsync(reader, 10));
    }

    [Fact]
    public async Task Unread_Chunks_That_Fall_Out_Of_The_Window_Are_Reported_Once_As_One_Loss()
    {
        var source = Source();
        var losses = new List<GapMarker>();
        using var reader = new RetainingAudioReader(source.Reader, capacity: 10, "interviewer", loss => { lock (losses) losses.Add(loss); });

        for (var i = 1; i <= 25; i++) await source.Writer.WriteAsync(Chunk(i), TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => reader.Produced == 25, "the pump");

        var first = await reader.ReadWithin();

        Assert.Equal(16, first.Sequence);
        lock (losses)
        {
            var loss = Assert.Single(losses);
            Assert.Equal("interviewer", loss.SourceId);
            Assert.Equal(1, loss.FirstMissingSequence);
            Assert.Equal(15, loss.LastMissingSequence);
            Assert.Equal(1500, loss.DurationMs);
            Assert.Equal(first.TimestampMs, loss.CaptureMs);
        }

        await ReadSequencesAsync(reader, 9);
        lock (losses) Assert.Single(losses);
    }

    [Fact]
    public async Task Chunks_That_Were_Read_Before_They_Fell_Out_Are_Not_A_Loss()
    {
        var source = Source();
        var losses = new List<GapMarker>();
        using var reader = new RetainingAudioReader(source.Reader, capacity: 5, "interviewer", loss => { lock (losses) losses.Add(loss); });

        for (var i = 1; i <= 30; i++)
        {
            await source.Writer.WriteAsync(Chunk(i), TestContext.Current.CancellationToken);
            Assert.Equal(i, (await reader.ReadWithin()).Sequence);
        }

        lock (losses) Assert.Empty(losses);
    }

    [Fact]
    public async Task The_End_Of_The_Source_Ends_The_Reader_After_The_Held_Chunks()
    {
        var source = Source();
        using var reader = new RetainingAudioReader(source.Reader, capacity: 10, "interviewer");
        await source.Writer.WriteAsync(Chunk(1), TestContext.Current.CancellationToken);
        await source.Writer.WriteAsync(Chunk(2), TestContext.Current.CancellationToken);
        source.Writer.Complete();

        Assert.Equal([1L, 2L], await ReadSequencesAsync(reader, 2));

        Assert.False(await reader.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask().Within());
        await reader.Completion.Within();
    }

    [Fact]
    public async Task A_Waiting_Consumer_Is_Woken_By_The_Next_Chunk_And_Released_By_Cancellation()
    {
        var source = Source();
        using var reader = new RetainingAudioReader(source.Reader, capacity: 10, "interviewer");

        var waiting = reader.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(waiting.IsCompleted);
        await source.Writer.WriteAsync(Chunk(1), TestContext.Current.CancellationToken);
        Assert.True(await waiting.Within());

        Assert.True(reader.TryRead(out var chunk));
        Assert.Equal(1, chunk.Sequence);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelled = reader.WaitToReadAsync(cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Within());
    }

    [Fact]
    public async Task A_Consumer_That_Is_Cancelled_Midway_Loses_Nothing_For_The_Next_One()
    {
        var source = Source();
        using var reader = new RetainingAudioReader(source.Reader, capacity: 50, "interviewer");
        for (var i = 1; i <= 10; i++) await source.Writer.WriteAsync(Chunk(i), TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => reader.Unread == 10, "the pump");

        using (var first = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            var seen = new List<long>();
            await foreach (var chunk in reader.ReadAllAsync(first.Token))
            {
                seen.Add(chunk.Sequence);
                if (seen.Count == 4) break; // the connection died here
            }

            Assert.Equal([1L, 2L, 3L, 4L], seen);
        }

        // The next connection continues where the last one stopped.
        Assert.Equal(Enumerable.Range(5, 6).Select(i => (long)i), await ReadSequencesAsync(reader, 6));
    }
}
