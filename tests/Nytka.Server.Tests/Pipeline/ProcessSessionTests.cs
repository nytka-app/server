using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Pipeline;

public sealed class ProcessSessionTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
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

    private NytkaApiFactory PaidServer()
    {
        var server = new NytkaApiFactory(db, settings =>
        {
            settings["Nytka:Stt:Batch:MinSeconds"] = "15";
            settings["Nytka:Stt:Batch:MaxSeconds"] = "45";
            settings["Nytka:Stt:Batch:HardMaxSeconds"] = "60";
            settings["Nytka:Stt:Batch:LongSilenceSeconds"] = "120";
        });
        _servers.Add(server);
        return server;
    }

    private async Task<int> RunAfterAsync(NytkaApiFactory server, int seconds)
    {
        server.Time.Advance(TimeSpan.FromSeconds(seconds));
        await server.Get<Scheduler>().TickAsync(default);
        return await server.RunJobsAsync();
    }

    [Fact]
    public async Task Default_rules_send_a_short_batch_alone_when_the_next_speech_is_a_minute_and_a_half_away()
    {
        var server = NewServer();
        await server.UploadAsync(Chunks(_session, Tone(1), Silence(90), Tone(10), Silence(2)));

        await server.RunJobsAsync();
        await RunAfterAsync(server, 61);

        Assert.Equal(2, await Count("transcription_batches"));
    }

    [Fact]
    public async Task A_short_batch_waits_for_the_next_speech_under_the_paid_rules()
    {
        var server = PaidServer();
        await server.UploadAsync(Chunks(_session, Tone(1), Silence(90), Tone(10), Silence(2)));

        await server.RunJobsAsync();
        await RunAfterAsync(server, 61);
        Assert.Equal(0, await Count("transcription_batches"));
        Assert.NotEqual(0, await Count("audio_chunks", "body is not null")); // the held speech keeps its audio

        await RunAfterAsync(server, 61);

        var (start, end) = await OnlyBatch();
        Assert.Equal(StartMs, start);
        Assert.InRange(end, StartMs + 101_000, StartMs + 101_500);
        Assert.Equal(0, await Count("audio_chunks", "body is not null"));
    }

    [Fact]
    public async Task A_lone_short_batch_is_flushed_when_the_session_goes_idle_under_the_paid_rules()
    {
        var server = PaidServer();
        await server.UploadAsync(Chunks(_session, Silence(1), Tone(1), Silence(2)));

        await server.RunJobsAsync();
        await RunAfterAsync(server, 61);
        Assert.Equal(0, await Count("transcription_batches"));

        await RunAfterAsync(server, 61);

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
    public async Task A_session_with_an_unreadable_chunk_gives_up_instead_of_looping_forever()
    {
        var server = NewServer();
        var now = server.Time.GetUtcNow();

        // Stored directly, bypassing the chunk and import endpoints: both now refuse to write a chunk
        // ChunkFormat cannot read, so this simulates a row from before that validation existed.
        await db.ExecuteAsync(
            "insert into capture_sessions (id, started_at, last_received_at) values (@session, @now, @now)",
            new { session = _session, now });
        await db.ExecuteAsync(
            """
            insert into audio_chunks (session_id, first_seq, frame_count, base_time, last_time, body, received_at)
            values (@session, 0, 1, @now, @now, @body, @now)
            """,
            new { session = _session, now, body = new byte[] { 0, 1, 2, 3 } }); // not a chunk: ChunkFormat.Read throws
        await server.Get<JobQueue>().EnqueueAsync(
            JobKinds.ProcessSession, new SessionPayload(_session), JobKinds.ProcessSessionKey(_session), now, default);

        await server.RunJobsAsync();                       // attempt 1
        Assert.Equal(0, await server.RunJobsAsync());      // waits 30 s
        server.Time.Advance(TimeSpan.FromSeconds(30));
        await server.RunJobsAsync();                       // attempt 2
        server.Time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(0, await server.RunJobsAsync());      // waits 2 min
        server.Time.Advance(TimeSpan.FromSeconds(1));
        await server.RunJobsAsync();                       // attempt 3, then gives up

        Assert.Equal(0, await Count("jobs"));
        Assert.NotNull(await db.ScalarAsync<DateTime?>(
            "select gave_up_at from capture_sessions where id = @session", new { session = _session }));

        // The scheduler leaves it alone from now on, instead of re-queuing it every tick forever.
        await server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await Count("jobs", "kind = 'process-session'"));
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
