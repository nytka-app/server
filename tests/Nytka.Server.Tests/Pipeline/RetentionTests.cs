using Nytka.Server.Jobs;

namespace Nytka.Server.Tests.Pipeline;

[Collection(PostgresCollection.Name)]
public sealed class RetentionTests(PostgresFixture db) : IAsyncLifetime
{
    private NytkaApiFactory _server = new(db);

    private DateTime Now => _server.Time.GetUtcNow().UtcDateTime;

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private async Task RunRetention()
    {
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    private async Task SpeechAudioEnded(DateTime endedAt, string status = "done")
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at)
            values (@id, @endedAt, @endedAt, 'closed', @endedAt, @endedAt);
            with batch as (
                insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at)
                values (@id, @endedAt, @endedAt, @status, '{}', @endedAt)
                returning id)
            insert into speech_audio (conversation_id, batch_id, started_at, ended_at, body)
            select @id, batch.id, @endedAt, @endedAt, '\x00'::bytea from batch;
            """,
            new { id, endedAt, status });
    }

    /// <summary>A chunk row inserted directly, so no process-session job touches it.</summary>
    private Task Chunk(DateTime receivedAt, bool processed)
    {
        var session = Guid.NewGuid();
        return db.ExecuteAsync(
            """
            insert into capture_sessions (id, started_at, last_received_at) values (@session, @receivedAt, @receivedAt);
            insert into audio_chunks (session_id, first_seq, frame_count, base_time, last_time, body, received_at, processed_at)
            values (@session, 0, 1, @receivedAt, @receivedAt,
                    case when @processed then null else '\x00'::bytea end, @receivedAt,
                    case when @processed then @receivedAt end);
            """,
            new { session, receivedAt, processed });
    }

    [Fact]
    public async Task Deletes_speech_audio_past_retention()
    {
        await SpeechAudioEnded(Now.AddDays(-15));
        await SpeechAudioEnded(Now.AddDays(-13));
        await SpeechAudioEnded(Now.AddDays(-20), status: "failed");

        await RunRetention();

        Assert.Equal(
            [Now.AddDays(-20), Now.AddDays(-13)],
            await db.QueryAsync<DateTime>("select ended_at from speech_audio order by ended_at"));
    }

    [Fact]
    public async Task Retention_zero_leaves_audio_to_transcription()
    {
        _server.Dispose();
        _server = new NytkaApiFactory(db, settings => settings["Nytka:Audio:RetentionDays"] = "0");
        await SpeechAudioEnded(Now.AddDays(-15));

        await RunRetention();

        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from speech_audio"));
    }

    [Fact]
    public async Task Deletes_processed_chunk_rows_after_seven_days()
    {
        await Chunk(Now.AddDays(-8), processed: true);
        await Chunk(Now.AddDays(-6), processed: true);
        await Chunk(Now.AddDays(-30), processed: false);

        await RunRetention();

        Assert.Equal(
            [Now.AddDays(-30), Now.AddDays(-6)],
            await db.QueryAsync<DateTime>("select received_at from audio_chunks order by received_at"));
    }

    [Fact]
    public async Task Deletes_diagnostics_older_than_thirty_days()
    {
        foreach (var at in new[] { Now.AddDays(-31), Now.AddDays(-29) })
        {
            await db.ExecuteAsync(
                "insert into diagnostics (id, at, received_at, payload) values (@id, @at, @at, '{}')",
                new { id = Guid.CreateVersion7(), at });
        }

        await RunRetention();

        Assert.Equal([Now.AddDays(-29)], await db.QueryAsync<DateTime>("select at from diagnostics"));
    }

    [Fact]
    public async Task Runs_again_a_day_later()
    {
        await RunRetention();
        await RunRetention();

        Assert.Equal(
            [Now.AddDays(1)],
            await db.QueryAsync<DateTime>("select run_after from jobs where kind = 'retention'"));
    }
}
