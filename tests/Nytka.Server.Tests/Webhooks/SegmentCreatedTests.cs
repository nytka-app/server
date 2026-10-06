using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Nytka.Server.Webhooks;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Webhooks;

/// <summary><c>segment.created</c>: one delivery per transcribed batch, ids and times only, and only to a webhook that names it.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SegmentCreatedTests(PostgresFixture db) : IAsyncLifetime
{
    private NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_batch_reaches_a_webhook_that_names_the_event_with_ids_and_no_text()
    {
        await using var receiver = await TestReceiver.StartAsync();
        var client = _server.CreateAuthorizedClient();
        (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = receiver.Url, events = new[] { "segment.created" } })).EnsureSuccessStatusCode();

        await _server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));
        await _server.RunJobsAsync();

        var request = Assert.Single(receiver.Requests);
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("segment.created", body.GetProperty("type").GetString());
        var data = body.GetProperty("data");
        var conversationId = await db.ScalarAsync<Guid>("select id from conversations");
        Assert.Equal(conversationId, data.GetProperty("conversationId").GetGuid());
        Assert.Equal(await db.ScalarAsync<long>("select id from transcription_batches"), data.GetProperty("batchId").GetInt64());
        var ids = await db.QueryAsync<long>("select id from segments order by started_at, id");
        Assert.Equal(ids, data.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("id").GetInt64()));
        Assert.All(data.GetProperty("segments").EnumerateArray(), s =>
        {
            Assert.True(s.TryGetProperty("startedAt", out _));
            Assert.True(s.TryGetProperty("endedAt", out _));
            Assert.False(s.TryGetProperty("text", out _));
        });
        var text = Encoding.UTF8.GetString(request.Body);
        Assert.DoesNotContain("hello", text, StringComparison.Ordinal);
        Assert.DoesNotContain("there", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_wildcard_does_not_match_it_but_naming_it_does()
    {
        await using var star = await TestReceiver.StartAsync();
        await using var named = await TestReceiver.StartAsync();
        var client = _server.CreateAuthorizedClient();
        (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = star.Url, events = new[] { "*" } })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = named.Url, events = new[] { "conversation.ready", "segment.created" } })).EnsureSuccessStatusCode();

        await _server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));
        await _server.RunJobsAsync();

        Assert.DoesNotContain(star.Requests, r => JsonDocument.Parse(r.Body).RootElement.GetProperty("type").GetString() == "segment.created");
        Assert.Contains(named.Requests, r => JsonDocument.Parse(r.Body).RootElement.GetProperty("type").GetString() == "segment.created");
    }

    [Fact]
    public async Task Each_batch_is_its_own_event()
    {
        await using var receiver = await TestReceiver.StartAsync();
        var client = _server.CreateAuthorizedClient();
        (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = receiver.Url, events = new[] { "segment.created" } })).EnsureSuccessStatusCode();

        await _server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(60), Tone(6), Silence(3)));
        await _server.RunJobsAsync();

        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from transcription_batches"));
        var ids = receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public void It_is_a_known_type_that_the_wildcard_skips()
    {
        Assert.Contains("segment.created", WebhookRecorder.Types);
        Assert.Contains("segment.created", WebhookRecorder.OptIn);
    }
}
