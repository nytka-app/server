using System.Net.Http.Json;
using System.Text.Json;

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

    [Fact]
    public async Task Status_reports_last_error()
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at)
            values (@id, now(), now(), 'open', now(), now());
            insert into transcription_batches (conversation_id, started_at, ended_at, status, error, offset_map, created_at)
            values (@id, now(), now(), 'failed', 'older failure', '{}', now() - interval '1 hour'),
                   (@id, now(), now(), 'failed', 'The transcription endpoint answered 503.', '{}', now()),
                   (@id, now(), now(), 'done', null, '{}', now() + interval '1 hour');
            """,
            new { id });

        var status = await Status();

        Assert.Equal("The transcription endpoint answered 503.", status.GetProperty("lastError").GetString());
    }
}
