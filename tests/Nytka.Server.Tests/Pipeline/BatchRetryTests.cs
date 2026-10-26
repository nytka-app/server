using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Pipeline;

/// <summary>A failed batch that still holds its WAV is transcribed again: by the scheduler on a doubling wait, or by the admin route at once.</summary>
public sealed class BatchRetryTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private Task<long> Count(string table, string where = "true") =>
        db.ScalarAsync<long>($"select count(*) from {table} where {where}");

    /// <summary>A batch the endpoint refused three times, as on an outage; the endpoint answers again afterwards.</summary>
    private async Task FailOneBatchAsync()
    {
        _server.Stt.Respond = _ => FakeStt.Json("{}", HttpStatusCode.ServiceUnavailable);
        await _server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));
        await _server.RunJobsAsync();
        _server.Time.Advance(TimeSpan.FromSeconds(30));
        await _server.RunJobsAsync();
        _server.Time.Advance(TimeSpan.FromMinutes(2));
        await _server.RunJobsAsync();
        Assert.Equal(0, await Count("transcription_batches", "status = 'pending'"));
        _server.Stt.Respond = _ => FakeStt.Json(FakeStt.DefaultJson);
    }

    /// <summary>Moves the failure back in time; <c>finished_at</c> follows the database clock, not the test's.</summary>
    private Task FailedAgo(TimeSpan ago, int retries) =>
        db.ExecuteAsync(
            "update transcription_batches set finished_at = now() - @ago, retries = @retries where status = 'failed'",
            new { ago, retries });

    private async Task TickAndRunAsync()
    {
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    [Fact]
    public async Task The_scheduler_transcribes_a_failed_batch_again_once_its_wait_is_over()
    {
        await FailOneBatchAsync();

        await TickAndRunAsync();
        Assert.Equal(1, await Count("transcription_batches", "status = 'failed'"));

        await FailedAgo(TimeSpan.FromMinutes(61), retries: 0);
        await TickAndRunAsync();

        Assert.Equal(1, await Count("transcription_batches", "status = 'done' and retries = 1 and wav is null and error is null"));
        Assert.Equal(2, await Count("segments"));
        Assert.Equal(
            await db.ScalarAsync<Guid>("select conversation_id from transcription_batches"),
            await db.ScalarAsync<Guid>("select distinct conversation_id from segments"));
    }

    [Theory]
    [InlineData(1, 119, false)]
    [InlineData(1, 121, true)]
    [InlineData(2, 239, false)]
    [InlineData(2, 241, true)]
    public async Task Each_round_waits_twice_as_long_as_the_one_before(int retries, int minutesAgo, bool due)
    {
        await FailOneBatchAsync();
        await FailedAgo(TimeSpan.FromMinutes(minutesAgo), retries);

        await _server.Get<BatchRetryQueue>().QueueAsync(force: false, default);

        Assert.Equal(due ? 1 : 0, await Count("jobs", "kind = 'transcribe'"));
        Assert.Equal(due ? "pending" : "failed", await db.ScalarAsync<string>("select status from transcription_batches"));
    }

    [Fact]
    public async Task The_scheduler_stops_after_the_last_round_and_a_round_that_fails_again_counts()
    {
        await FailOneBatchAsync();
        await FailedAgo(TimeSpan.FromDays(30), retries: BatchRetryQueue.MaxRetries);

        await TickAndRunAsync();
        Assert.Equal(1, await Count("transcription_batches", "status = 'failed'"));
        Assert.Equal(0, await Count("jobs", "kind = 'transcribe'"));

        // The round before the last fails once more and is not retried after it.
        _server.Stt.Respond = _ => FakeStt.Json("{}", HttpStatusCode.ServiceUnavailable);
        await FailedAgo(TimeSpan.FromDays(30), retries: BatchRetryQueue.MaxRetries - 1);
        await TickAndRunAsync();
        _server.Time.Advance(TimeSpan.FromSeconds(30));
        await _server.RunJobsAsync();
        _server.Time.Advance(TimeSpan.FromMinutes(2));
        await _server.RunJobsAsync();

        Assert.Equal(1, await Count("transcription_batches", $"status = 'failed' and retries = {BatchRetryQueue.MaxRetries}"));
        Assert.Equal(6, _server.Stt.Requests.Count);
    }

    [Fact]
    public async Task The_retry_route_queues_every_failed_batch_with_audio_now_and_starts_its_rounds_over()
    {
        await FailOneBatchAsync();
        await FailedAgo(TimeSpan.Zero, retries: BatchRetryQueue.MaxRetries);
        await FailOneBatchAsync();
        await db.ExecuteAsync("update transcription_batches set wav = null where status = 'failed' and retries = 0");
        var client = _server.CreateAuthorizedClient();

        var response = await client.PostAsync("/api/v1/transcriptions/retry", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetInt32());
        Assert.Equal(JobPriority.Late, await db.ScalarAsync<short>("select priority from jobs where kind = 'transcribe'"));

        await _server.RunJobsAsync();
        Assert.Equal(1, await Count("transcription_batches", "status = 'done' and retries = 0"));
        Assert.Equal(1, await Count("transcription_batches", "status = 'failed' and wav is null"));
    }

    [Fact]
    public async Task New_lines_in_a_summarized_conversation_make_it_due_for_another_summary()
    {
        await FailOneBatchAsync();
        var conversationId = await db.ScalarAsync<Guid>("select conversation_id from transcription_batches");
        await db.ExecuteAsync(
            "update conversations set status = 'closed', ai_status = 'done', ai_through_segment_id = null where id = @conversationId",
            new { conversationId });
        var conversations = _server.Get<ConversationStore>();
        var now = _server.Time.GetUtcNow();
        Assert.DoesNotContain(conversationId, await conversations.DueForEnrichmentAsync(now.AddDays(-30), now, 3, 100, default));

        await _server.CreateAuthorizedClient().PostAsync("/api/v1/transcriptions/retry", null);
        await _server.RunJobsAsync();

        Assert.Contains(conversationId, await conversations.DueForEnrichmentAsync(now.AddDays(-30), now, 3, 100, default));
    }
}
