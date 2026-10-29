using Nytka.Storage;

namespace Nytka.Server.Tests.Storage;

/// <summary><see cref="ChunkStore.LoadWindowAsync"/> picks the window from frame counts and reads only its bodies.</summary>
public sealed class ChunkWindowTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private static readonly DateTime T0 = new(2026, 10, 29, 8, 0, 0, DateTimeKind.Utc);

    private readonly Guid _session = Guid.NewGuid();

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>One chunk per frame count, back to back from sequence 0; a null count is a processed chunk without a body.</summary>
    private async Task Store(params int?[] frameCounts)
    {
        await db.ExecuteAsync(
            "insert into capture_sessions (id, started_at, last_received_at) values (@_session, @T0, @T0)",
            new { _session, T0 });
        long seq = 0;
        foreach (var count in frameCounts)
        {
            var frames = count ?? 1;
            await db.ExecuteAsync(
                """
                insert into audio_chunks (session_id, first_seq, frame_count, base_time, last_time, body, received_at)
                values (@_session, @seq, @frames, @T0, @T0, @body, @T0)
                """,
                new { _session, seq, frames, T0, body = count is null ? null : new byte[] { (byte)seq } });
            seq += frames;
        }
    }

    private Task<(IReadOnlyList<PendingChunk> Window, bool More)> Load(int minFrames, int maxChunks) =>
        new ChunkStore(db.DataSource).LoadWindowAsync(_session, minFrames, maxChunks, default);

    [Fact]
    public async Task Takes_chunks_until_the_frames_before_reach_the_minimum()
    {
        await Store(400, 400, 400, 400);

        var (window, more) = await Load(minFrames: 1000, maxChunks: 10);

        // 0 and 400 and 800 frames came before the first three; the fourth starts at 1200.
        Assert.Equal([0L, 400, 800], window.Select(c => c.FirstSeq));
        Assert.All(window, c => Assert.NotEmpty(c.Body));
        Assert.True(more);
    }

    [Fact]
    public async Task Stops_at_the_chunk_cap()
    {
        await Store(10, 10, 10);

        var (window, more) = await Load(minFrames: 1000, maxChunks: 2);

        Assert.Equal(2, window.Count);
        Assert.True(more);
    }

    [Fact]
    public async Task Skips_processed_chunks_and_says_when_nothing_is_left()
    {
        await Store(null, 300, 300);

        var (window, more) = await Load(minFrames: 1000, maxChunks: 10);

        Assert.Equal([1L, 301], window.Select(c => c.FirstSeq));
        Assert.All(window, c => Assert.NotEmpty(c.Body));
        Assert.False(more);
    }

    [Fact]
    public async Task An_empty_session_has_no_window()
    {
        await Store((int?)null);

        var (window, more) = await Load(minFrames: 1000, maxChunks: 10);

        Assert.Empty(window);
        Assert.False(more);
    }
}
