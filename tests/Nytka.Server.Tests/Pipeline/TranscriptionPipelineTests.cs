using System.Net;
using Nytka.Audio.Batching;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Transcription;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Pipeline;

[Collection(PostgresCollection.Name)]
public sealed class TranscriptionPipelineTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly Guid _session = Guid.NewGuid();
    private NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private Task<long> Count(string table, string where = "true") =>
        db.ScalarAsync<long>($"select count(*) from {table} where {where}");

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    [Fact]
    public async Task Speech_becomes_a_transcribed_conversation()
    {
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(3)));

        await _server.RunJobsAsync();

        var request = Assert.Single(_server.Stt.Requests);
        Assert.Equal("RIFF"u8.ToArray(), request.File[..4]);
        Assert.Equal(["hello", "there"], await db.QueryAsync<string>("select text from segments order by started_at"));
        Assert.Equal(
            [StartMs, StartMs + 1_000],
            (await db.QueryAsync<DateTime>("select started_at from segments order by started_at")).Select(Ms));
        Assert.Equal(1, await Count("transcription_batches", "status = 'done' and wav is null and response is not null"));
        Assert.Equal(1, await Count("conversations", "status = 'open'"));
    }

    [Fact]
    public async Task Long_gap_splits_conversations()
    {
        // The pendant went quiet: capture time jumps 150 s with no frames in between.
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(1), Gap(150), Tone(6), Silence(3)));

        await _server.RunJobsAsync();

        Assert.Equal(2, await Count("conversations"));
        Assert.Equal(2, await Count("transcription_batches"));
    }

    [Fact]
    public async Task Long_silence_splits_conversations()
    {
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(150), Tone(6), Silence(3)));

        await _server.RunJobsAsync();

        Assert.Equal(2, await Count("conversations"));
        Assert.Equal(
            [2L, 2L],
            await db.QueryAsync<long>("select count(*) from segments group by conversation_id order by min(started_at)"));
    }

    [Fact]
    public async Task Short_gap_keeps_one_conversation()
    {
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(60), Tone(6), Silence(3)));

        await _server.RunJobsAsync();

        Assert.Equal(1, await Count("conversations"));
        Assert.Equal(2, await Count("transcription_batches"));
    }

    [Fact]
    public async Task Endpoint_failure_marks_batch_failed_after_three_attempts()
    {
        _server.Stt.Respond = _ => FakeStt.Json("{}", HttpStatusCode.ServiceUnavailable);
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(3)));

        await _server.RunJobsAsync();
        _server.Time.Advance(TimeSpan.FromSeconds(30));
        await _server.RunJobsAsync();
        _server.Time.Advance(TimeSpan.FromMinutes(2));
        await _server.RunJobsAsync();

        Assert.Equal(3, _server.Stt.Requests.Count);
        Assert.Equal(
            "The transcription endpoint answered 503.",
            await db.ScalarAsync<string>("select error from transcription_batches where status = 'failed'"));
        Assert.Equal(1, await Count("speech_audio"));
        Assert.Equal(1, await Count("transcription_batches", "status = 'failed' and finished_at is not null"));
        Assert.Equal(0, await Count("jobs", "kind = 'transcribe'"));
    }

    [Fact]
    public async Task Text_without_segments_becomes_one_segment()
    {
        _server.Stt.Respond = _ => FakeStt.Json("""{"text":"just words"}""");
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(3)));

        await _server.RunJobsAsync();

        var (start, end, text) = Assert.Single(await db.QueryAsync<(DateTime, DateTime, string)>(
            "select s.started_at, s.ended_at, s.text from segments s"));
        Assert.Equal("just words", text);
        Assert.Equal(await db.ScalarAsync<DateTime>("select started_at from transcription_batches"), start);
        Assert.Equal(await db.ScalarAsync<DateTime>("select ended_at from transcription_batches"), end);
    }

    [Fact]
    public async Task Speakers_from_the_answer_are_stored_and_missing_ones_are_null()
    {
        _server.Stt.Respond = _ => FakeStt.Json(
            """{"text":"a b c","segments":[{"start":0,"end":1,"text":"a","speaker":"SPEAKER_00"},{"start":1,"end":2,"text":"b","speaker":1},{"start":2,"end":3,"text":"c"}]}""");
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(3)));

        await _server.RunJobsAsync();

        Assert.Equal(
            [("a", "SPEAKER_00"), ("b", "1"), ("c", null)],
            await db.QueryAsync<(string, string?)>("select text, speaker from segments order by started_at"));
    }

    [Fact]
    public async Task The_voice_id_and_the_wearer_flag_are_stored()
    {
        _server.Stt.Respond = _ => FakeStt.Json(
            """{"text":"a b c","segments":[{"start":0,"end":1,"text":"a","speaker":"SPEAKER_0","speaker_id":"0","is_user":true},{"start":1,"end":2,"text":"b","speaker":"SPEAKER_4","speaker_id":"4","is_user":false},{"start":2,"end":3,"text":"c"}]}""");
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(3)));

        await _server.RunJobsAsync();

        Assert.Equal(
            [("a", "0", (bool?)true), ("b", "4", false), ("c", null, null)],
            await db.QueryAsync<(string, string?, bool?)>("select text, speaker_id, is_user from segments order by started_at"));
    }

    [Fact]
    public async Task A_finished_batch_records_when_it_finished()
    {
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(3)));
        Assert.Equal(0, await Count("transcription_batches", "finished_at is not null"));

        await _server.RunJobsAsync();

        Assert.Equal(1, await Count("transcription_batches", "status = 'done' and finished_at is not null"));
    }

    [Fact]
    public async Task Empty_transcript_writes_no_segments()
    {
        _server.Stt.Respond = _ => FakeStt.Json("""{"text":"","segments":[]}""");
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(3)));

        await _server.RunJobsAsync();

        Assert.Equal(0, await Count("segments"));
        Assert.Equal(1, await Count("transcription_batches", "status = 'done'"));
    }

    [Fact]
    public async Task Retention_zero_drops_speech_audio_once_transcribed()
    {
        _server.Dispose();
        _server = new NytkaApiFactory(db, settings => settings["Nytka:Audio:RetentionDays"] = "0");
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(3)));

        await _server.RunJobsAsync();

        Assert.Equal(0, await Count("speech_audio"));
        Assert.Equal(2, await Count("segments"));
    }

    [Fact]
    public async Task Idle_conversations_close_after_the_gap()
    {
        await _server.UploadAsync(Chunks(_session, Tone(6), Silence(3)));
        await _server.RunJobsAsync();

        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
        Assert.Equal(1, await Count("conversations", "status = 'open'"));

        _server.Time.Advance(TimeSpan.FromMinutes(3));
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
        Assert.Equal(1, await Count("conversations", "status = 'closed'"));
    }

    [Fact]
    public void Segments_follow_capture_times_across_a_gap()
    {
        // 400 ms of speech at t = 1 s, then 200 ms at t = 5.1 s, back to back in the WAV.
        var map = new OffsetMap([new OffsetMap.Entry(0, 1_000), new OffsetMap.Entry(400, 5_100)], totalMs: 600);
        var result = new TranscriptionResult(
            "a b", [new TranscribedSegment(0.1, 0.3, "a"), new TranscribedSegment(0.45, 0.55, "b")], "{}");

        var segments = TranscribeHandler.ToSegments(result, map, DateTime.UnixEpoch, DateTime.UnixEpoch);

        Assert.Equal(
            [(1_100L, 1_300L), (5_150L, 5_250L)],
            segments.Select(s => (s.StartedAt.ToUnixTimeMilliseconds(), s.EndedAt.ToUnixTimeMilliseconds())));
    }
}
