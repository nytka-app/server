using Nytka.Server.Jobs;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Pipeline;

[Collection(PostgresCollection.Name)]
public sealed class ProcessSessionTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly Guid _session = Guid.NewGuid();
    private readonly List<NytkaApiFactory> _servers = [];

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _servers.ForEach(s => s.Dispose());
        return Task.CompletedTask;
    }

    private NytkaApiFactory NewServer()
    {
        var server = new NytkaApiFactory(db);
        _servers.Add(server);
        return server;
    }

    private Task<long> Count(string table, string where = "true") =>
        db.ScalarAsync<long>($"select count(*) from {table} where {where}");

    private async Task<(long Start, long End)> OnlyBatch()
    {
        var (start, end) = Assert.Single(
            await db.QueryAsync<(DateTime, DateTime)>("select started_at, ended_at from transcription_batches"));
        return (new DateTimeOffset(start).ToUnixTimeMilliseconds(), new DateTimeOffset(end).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Silence_is_discarded()
    {
        var server = NewServer();
        await server.UploadAsync(Chunks(_session, Silence(5)));

        await server.RunJobsAsync();

        Assert.Equal(0, await Count("transcription_batches"));
        Assert.Equal(0, await Count("audio_chunks", "body is not null"));
        Assert.Equal(1, await Count("audio_chunks", "processed_at is not null"));
    }

    [Fact]
    public async Task Speech_then_a_pause_becomes_a_batch()
    {
        var server = NewServer();
        await server.UploadAsync(Chunks(_session, Tone(6), Silence(3)));

        await server.RunJobsAsync();

        var (start, end) = await OnlyBatch();
        Assert.Equal(StartMs, start);                           // padding clamped to the first frame
        Assert.InRange(end, StartMs + 6_000, StartMs + 6_300);  // 200 ms padding after the tone
        Assert.Equal(1, await Count("conversations"));
        Assert.Equal(1, await Count("speech_audio"));
        Assert.Equal(1, await Count("transcription_batches", "status = 'done'")); // RunJobsAsync now also transcribes, which drops the WAV
        Assert.Equal(0, await Count("audio_chunks", "body is not null"));
    }

    [Fact]
    public async Task Open_speech_waits_until_the_session_goes_idle()
    {
        var server = NewServer();
        await server.UploadAsync(Chunks(_session, Silence(1), Tone(3)));

        await server.RunJobsAsync();
        Assert.Equal(0, await Count("transcription_batches"));
        Assert.Equal(1, await Count("audio_chunks", "body is not null"));

        server.Time.Advance(TimeSpan.FromSeconds(61));
        await server.Get<Scheduler>().TickAsync(default);
        await server.RunJobsAsync();

        Assert.Equal(1, await Count("transcription_batches"));
        Assert.Equal(0, await Count("audio_chunks", "body is not null"));
    }

    [Fact]
    public async Task Restart_between_runs_loses_no_speech()
    {
        var before = NewServer();
        await before.UploadAsync(Chunks(_session, Silence(1), Tone(3)));
        await before.RunJobsAsync();
        before.Dispose();

        var after = NewServer();
        after.Time.Advance(TimeSpan.FromSeconds(61));
        await after.Get<Scheduler>().TickAsync(default);
        await after.RunJobsAsync();

        var (start, end) = await OnlyBatch();
        Assert.InRange(start, StartMs + 700, StartMs + 1_000);
        Assert.Equal(StartMs + 4_000, end);
    }

    [Fact]
    public async Task Waits_for_a_missing_chunk_then_carries_on()
    {
        var server = NewServer();
        var chunks = Chunks(_session, Silence(90)); // sequences 0, 1500 and 3000
        await server.UploadAsync([chunks[0], chunks[2]]);

        await server.RunJobsAsync();

        Assert.Equal(0, await Count("audio_chunks", "first_seq = 0 and body is not null"));
        Assert.Equal(1, await Count("audio_chunks", "first_seq = 3000 and body is not null"));
        Assert.Equal(
            server.Time.GetUtcNow().AddMinutes(1).UtcDateTime,
            await db.ScalarAsync<DateTime>("select run_after from jobs where kind = 'process-session'"));

        server.Time.Advance(TimeSpan.FromMinutes(10));
        await server.RunJobsAsync();

        Assert.Equal(0, await Count("audio_chunks", "body is not null"));
    }

    private async Task<int> DrainAsync(NytkaApiFactory server)
    {
        // A run that spins without progress trips the timeout instead of hanging the suite.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ran = await server.Get<JobRunner>().RunDueJobsAsync(cts.Token);
        server.Time.Advance(TimeSpan.FromSeconds(61));
        await server.Get<Scheduler>().TickAsync(default);
        return ran + await server.Get<JobRunner>().RunDueJobsAsync(cts.Token);
    }

    [Fact]
    public async Task Tiny_chunks_drain_completely_in_bounded_runs()
    {
        var server = NewServer();
        var parts = Enumerable.Range(0, 39).SelectMany(_ => new[] { Tone(20), Silence(3) }).ToArray();
        var chunks = Chunks(_session, 9, parts);
        Assert.True(chunks.Count > 4_900);
        await server.UploadAsync(chunks);

        var ran = await DrainAsync(server);

        var batches = await db.QueryAsync<(DateTime, DateTime)>(
            "select started_at, ended_at from transcription_batches order by started_at");
        Assert.Equal(39, batches.Count());
        Assert.All(batches, b => Assert.InRange((b.Item2 - b.Item1).TotalSeconds, 20, 21));
        Assert.Equal(0, await Count("audio_chunks", "body is not null"));
        Assert.InRange(ran, 1, 39 * 2 + 20); // one transcription per batch plus a few process runs
    }

    [Fact]
    public async Task Tiny_chunks_of_unbroken_speech_drain_through_the_hard_max()
    {
        var server = NewServer();
        var chunks = Chunks(_session, 9, Tone(900));
        await server.UploadAsync(chunks);

        var ran = await DrainAsync(server);

        var batches = (await db.QueryAsync<(DateTime, DateTime)>(
            "select started_at, ended_at from transcription_batches order by started_at")).ToList();
        Assert.InRange(batches.Count, 20, 45);
        Assert.All(batches, b => Assert.InRange((b.Item2 - b.Item1).TotalSeconds, 1, 46));
        Assert.Equal(0, await Count("audio_chunks", "body is not null"));
        Assert.InRange(ran, 1, batches.Count * 2 + 20);
    }
}
