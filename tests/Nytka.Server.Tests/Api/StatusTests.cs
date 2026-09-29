using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class StatusTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private Task<JsonElement> Status() =>
        _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/status");

    [Fact]
    public async Task Status_reports_pending_chunks()
    {
        var empty = await Status();
        var session = Guid.NewGuid();
        await _server.UploadAsync([TestChunks.Build(session, 0, 5), TestChunks.Build(session, 5, 5)]);

        var status = await Status();

        Assert.Equal(0, empty.GetProperty("pendingChunks").GetInt64());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("oldestPendingAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lastError").ValueKind);
        Assert.Equal(2, status.GetProperty("pendingChunks").GetInt64());
        Assert.Equal("2026-09-29T10:00:00Z", status.GetProperty("oldestPendingAt").GetString());
    }

    private async Task<Guid> SeedConversation(string aiStatus = "none")
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, ai_status, created_at, updated_at)
            values (@id, now(), now(), 'closed', @aiStatus, now(), now())
            """,
            new { id, aiStatus });
        return id;
    }

    private Task SeedBatch(Guid conversation, string status, string? error, string finishedAt) =>
        db.ExecuteAsync(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, error, offset_map, created_at, finished_at)
            values (@conversation, now(), now(), @status, @error, '{}', now(), cast(@finishedAt as timestamptz))
            """,
            new { conversation, status, error, finishedAt });

    [Fact]
    public async Task Status_reports_a_current_failure()
    {
        var id = await SeedConversation();
        await SeedBatch(id, "failed", "older failure", "2026-09-29T08:00:00Z");
        await SeedBatch(id, "failed", "The transcription endpoint answered 503.", "2026-09-29T09:00:00Z");

        var status = await Status();

        Assert.Equal("The transcription endpoint answered 503.", status.GetProperty("lastError").GetString());
        Assert.Equal("2026-09-29T09:00:00Z", status.GetProperty("lastErrorAt").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastSuccessAt").ValueKind);
    }

    [Fact]
    public async Task A_later_batch_that_transcribes_clears_the_failure_and_keeps_the_history()
    {
        var id = await SeedConversation();
        await SeedBatch(id, "failed", "The transcription endpoint answered 503.", "2026-09-29T09:00:00Z");
        await SeedBatch(id, "done", null, "2026-09-29T09:30:00Z");

        var status = await Status();

        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastError").ValueKind);
        Assert.Equal("2026-09-29T09:00:00Z", status.GetProperty("lastErrorAt").GetString());
        Assert.Equal("2026-09-29T09:30:00Z", status.GetProperty("lastSuccessAt").GetString());
    }

    [Fact]
    public async Task A_batch_that_finished_before_the_failure_does_not_clear_it()
    {
        var id = await SeedConversation();
        await SeedBatch(id, "done", null, "2026-09-29T08:00:00Z");
        await SeedBatch(id, "failed", "The transcription endpoint answered 503.", "2026-09-29T09:00:00Z");

        var status = await Status();

        Assert.Equal("The transcription endpoint answered 503.", status.GetProperty("lastError").GetString());
        Assert.Equal("2026-09-29T08:00:00Z", status.GetProperty("lastSuccessAt").GetString());
    }

    [Fact]
    public async Task A_failure_from_before_finished_at_existed_is_cleared_by_any_finished_batch()
    {
        var id = await SeedConversation();
        await db.ExecuteAsync(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, error, offset_map, created_at)
            values (@id, now(), now(), 'failed', 'An old failure.', '{}', now())
            """,
            new { id });

        Assert.Equal("An old failure.", (await Status()).GetProperty("lastError").GetString());

        await SeedBatch(id, "done", null, "2026-09-29T09:30:00Z");

        var status = await Status();
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastError").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("lastErrorAt").ValueKind);
    }

    [Fact]
    public async Task A_batch_that_fails_and_one_that_completes_stamp_finished_at()
    {
        _server.Stt.Respond = _ => FakeStt.Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
        await _server.UploadAsync(SyntheticAudio.Chunks(Guid.NewGuid(), SyntheticAudio.Tone(6), SyntheticAudio.Silence(3)));
        await _server.RunJobsAsync();
        _server.Time.Advance(TimeSpan.FromSeconds(30));
        await _server.RunJobsAsync();
        _server.Time.Advance(TimeSpan.FromMinutes(2));
        await _server.RunJobsAsync();
        var failed = await Status();

        _server.Stt.Respond = _ => FakeStt.Json(FakeStt.DefaultJson);
        await _server.UploadAsync(SyntheticAudio.Chunks(Guid.NewGuid(), SyntheticAudio.Tone(6), SyntheticAudio.Silence(3)));
        await _server.RunJobsAsync();
        var recovered = await Status();

        Assert.Equal("The transcription endpoint answered 503.", failed.GetProperty("lastError").GetString());
        Assert.Equal(JsonValueKind.String, failed.GetProperty("lastErrorAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("lastSuccessAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, recovered.GetProperty("lastError").ValueKind);
        Assert.True(
            recovered.GetProperty("lastSuccessAt").GetDateTime() > recovered.GetProperty("lastErrorAt").GetDateTime());
    }

    [Fact]
    public async Task Status_reports_the_ai_block()
    {
        var empty = (await Status()).GetProperty("ai");
        await SeedConversation("pending");
        await SeedConversation("pending");

        var ai = (await Status()).GetProperty("ai");

        Assert.False(empty.GetProperty("configured").GetBoolean());
        Assert.Equal(0, empty.GetProperty("pending").GetInt64());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lastError").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lastErrorAt").ValueKind);
        Assert.Equal(2, ai.GetProperty("pending").GetInt64());
    }

    [Fact]
    public async Task An_ai_failure_clears_once_a_later_run_finishes()
    {
        await SeedConversation("failed");
        await db.ExecuteAsync(
            "update conversations set ai_message = 'The language model endpoint answered 429.', ai_updated_at = '2026-09-29T09:00:00Z'");

        var current = (await Status()).GetProperty("ai");
        var done = await SeedConversation("done");
        await db.ExecuteAsync("update conversations set ai_updated_at = '2026-09-29T09:30:00Z' where id = @done", new { done });
        var cleared = (await Status()).GetProperty("ai");

        Assert.Equal("The language model endpoint answered 429.", current.GetProperty("lastError").GetString());
        Assert.Equal("2026-09-29T09:00:00Z", current.GetProperty("lastErrorAt").GetString());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("lastError").ValueKind);
        Assert.Equal("2026-09-29T09:00:00Z", cleared.GetProperty("lastErrorAt").GetString());
    }

    [Fact]
    public async Task An_ai_failure_survives_a_retry_until_a_run_finishes()
    {
        var id = await SeedConversation("failed");
        await db.ExecuteAsync(
            "update conversations set ai_message = 'The language model endpoint answered 429.', ai_updated_at = '2026-09-29T09:00:00Z'");

        await db.ExecuteAsync("update conversations set ai_status = 'pending'");
        var retrying = (await Status()).GetProperty("ai");
        await db.ExecuteAsync(
            "update conversations set ai_status = 'done', ai_message = null, ai_updated_at = '2026-09-29T10:00:00Z' where id = @id",
            new { id });
        var finished = (await Status()).GetProperty("ai");

        Assert.Equal("The language model endpoint answered 429.", retrying.GetProperty("lastError").GetString());
        Assert.Equal("2026-09-29T09:00:00Z", retrying.GetProperty("lastErrorAt").GetString());
        Assert.Equal(1, retrying.GetProperty("pending").GetInt64());
        Assert.Equal(JsonValueKind.Null, finished.GetProperty("lastError").ValueKind);
    }

    [Fact]
    public async Task A_pending_conversation_that_was_only_skipped_is_no_failure()
    {
        await SeedConversation("pending");
        await db.ExecuteAsync(
            "update conversations set ai_message = 'Too short to summarize.', ai_updated_at = '2026-09-29T09:00:00Z'");

        var ai = (await Status()).GetProperty("ai");

        Assert.Equal(JsonValueKind.Null, ai.GetProperty("lastError").ValueKind);
        Assert.Equal(JsonValueKind.Null, ai.GetProperty("lastErrorAt").ValueKind);
    }

    [Fact]
    public async Task Status_reports_the_model_as_configured_when_it_is()
    {
        using var configured = new NytkaApiFactory(db, services: s => s.AddSingleton<Nytka.Server.Ai.ILlmClient>(new FakeLlm()));
        var client = configured.CreateAuthorizedClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/status");

        Assert.True(status.GetProperty("ai").GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Status_needs_an_admin_token()
    {
        var response = await _server.CreateClient().GetAsync("/api/v1/status");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
