using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class ChunkUploadTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _factory = new(db);
    private readonly Guid _session = Guid.NewGuid();

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> Upload(uint firstSeq, int frames) =>
        _factory.CreateAuthorizedClient().PostAsync("/api/v1/chunks", TestChunks.Content(TestChunks.Build(_session, firstSeq, frames)));

    private async Task<long> Count(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(sql);
    }

    [Fact]
    public async Task Stores_a_new_chunk()
    {
        var response = await Upload(0, 10);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(9, body.GetProperty("acceptedThroughSeq").GetInt64());
        Assert.Equal(1, await Count("select count(*) from audio_chunks where body is not null"));
        Assert.Equal(1, await Count("select count(*) from jobs where kind = 'process-session'"));
    }

    [Fact]
    public async Task Same_chunk_again_is_a_duplicate()
    {
        await Upload(0, 10);

        var response = await Upload(0, 10);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(9, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acceptedThroughSeq").GetInt64());
        Assert.Equal(1, await Count("select count(*) from audio_chunks"));
        Assert.Equal(1, await Count("select count(*) from jobs"));
    }

    [Fact]
    public async Task Retry_after_processing_is_a_duplicate()
    {
        await Upload(0, 10);
        await using (var connection = await db.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("update audio_chunks set body = null, processed_at = now()");
        }

        var response = await Upload(0, 10);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await Count("select count(*) from audio_chunks where body is not null"));
    }

    [Fact]
    public async Task Overlapping_chunk_is_a_conflict()
    {
        await Upload(0, 10);

        Assert.Equal(HttpStatusCode.Conflict, (await Upload(5, 10)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(0, 5)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Upload(10, 10)).StatusCode);
    }

    [Fact]
    public async Task Malformed_chunk_is_rejected()
    {
        var garbage = Enumerable.Repeat((byte)'x', 40).ToArray(); // header-sized, wrong magic
        var response = await _factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/chunks", TestChunks.Content(garbage));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("NYTK", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_chunk_is_rejected()
    {
        var response = await _factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/chunks", TestChunks.Content(new byte[600 * 1024]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Chunks_need_a_token()
    {
        var response = await _factory.CreateClient()
            .PostAsync("/api/v1/chunks", TestChunks.Content(TestChunks.Build(_session, 0, 1)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
