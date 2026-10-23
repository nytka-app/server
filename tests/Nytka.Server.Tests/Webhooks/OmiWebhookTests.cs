using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Nytka.Server.Tests.Ai;
using Nytka.Server.Webhooks;

namespace Nytka.Server.Tests.Webhooks;

/// <summary>The <c>omi</c> webhook format: Omi's memory-creation payload, signed and retried like the default one.</summary>
[Collection(PostgresCollection.Name)]
public sealed class OmiWebhookTests(PostgresFixture db) : AiTestBase(db)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 8, 58, 0, TimeSpan.Zero);

    private async Task<Guid> SeedGolden()
    {
        var id = Guid.Parse("0198a1c4-7d2e-7b1a-9c3f-5e6d7a8b9c0d");
        await Db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, title, ai_summary, created_at, updated_at)
            values (@id, @start, @end, 'closed', 'Lunch with Anna', 'Planned the weekend.', @end, @end)
            """,
            new { id, start = Start, end = Start.AddMinutes(2) });
        await Db.ExecuteAsync(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, response, created_at, finished_at)
            values (@id, @start, @end, 'done', '{}', '{}', @start, @end)
            """,
            new { id, start = Start, end = Start.AddMinutes(2) });
        await AddSegment(id, "Call Ben tomorrow.", Start, "A", "spk-a", true);
        await AddSegment(id, "Sure, I will.", Start.AddSeconds(3), "B", "spk-b", false);
        await AddSegment(id, "Back to me.", Start.AddSeconds(7), "A", "spk-a", true);
        await AddSegment(id, "Unlabelled.", Start.AddSeconds(9));
        await Db.ExecuteAsync("update segments set ended_at = started_at + interval '2.5 seconds' where conversation_id = @id", new { id });
        await Db.ExecuteAsync("update segments set speech_kind = 'media' where text = 'Unlabelled.'");
        await Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, done, created_at, updated_at) values
                (@a, @id, 'Call Ben', 'call ben', false, @start, @start),
                (@b, @id, 'Buy milk', 'buy milk', true, @start, @start)
            """,
            new { id, a = Guid.Parse("00000000-0000-7000-8000-000000000001"), b = Guid.Parse("00000000-0000-7000-8000-000000000002"), start = Start });
        return id;
    }

    [Fact]
    public async Task The_payload_matches_the_golden_json()
    {
        var id = await SeedGolden();
        await using var connection = await Server.Get<NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var body = await OmiPayload.BuildAsync(id, connection, transaction, default);

        var golden =
            """
            {
              "id": "0198a1c4-7d2e-7b1a-9c3f-5e6d7a8b9c0d",
              "created_at": "2026-10-23T09:00:00Z",
              "started_at": "2026-10-23T08:58:00Z",
              "finished_at": "2026-10-23T09:00:00Z",
              "transcript_segments": [
                { "text": "Call Ben tomorrow.", "speaker": "SPEAKER_00", "speakerId": 0, "speaker_name": "Wearer", "is_user": true, "start": 0.0, "end": 2.5 },
                { "text": "Sure, I will.", "speaker": "SPEAKER_01", "speakerId": 1, "speaker_name": "B", "is_user": false, "start": 3.0, "end": 5.5 },
                { "text": "Back to me.", "speaker": "SPEAKER_00", "speakerId": 0, "speaker_name": "Wearer", "is_user": true, "start": 7.0, "end": 9.5 }
              ],
              "structured": {
                "title": "Lunch with Anna",
                "overview": "Planned the weekend.",
                "emoji": null,
                "category": null,
                "action_items": [
                  { "description": "Call Ben", "completed": false },
                  { "description": "Buy milk", "completed": true }
                ],
                "events": []
              },
              "apps_response": [],
              "discarded": false,
              "folder_id": null,
              "folder_name": null
            }
            """;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(golden), JsonNode.Parse(body!)), body);
    }

    [Fact]
    public async Task A_conversation_that_is_gone_has_no_payload()
    {
        await using var connection = await Server.Get<NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        Assert.Null(await OmiPayload.BuildAsync(Guid.NewGuid(), connection, transaction, default));
    }

    [Fact]
    public async Task An_omi_webhook_gets_the_memory_payload_signed_and_a_default_one_still_gets_the_envelope()
    {
        await using var omi = await TestReceiver.StartAsync();
        await using var plain = await TestReceiver.StartAsync();
        var client = Server.CreateAuthorizedClient();
        var created = await (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = omi.Url, events = new[] { "*" }, format = "omi" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = plain.Url, events = new[] { "*" } })).EnsureSuccessStatusCode();
        Assert.Equal("omi", created.GetProperty("format").GetString());
        var id = await Seed(Talk, Talk);

        await TickAndRun();

        var request = Assert.Single(omi.Requests);
        var body = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.False(body.TryGetProperty("type", out _));
        Assert.Equal("Lunch with Anna", body.GetProperty("structured").GetProperty("title").GetString());
        Assert.Equal(2, body.GetProperty("transcript_segments").GetArrayLength());
        Assert.Equal("conversation.ready", request.Headers["Nytka-Event"]);
        Assert.Equal(
            WebhookSigner.Sign(created.GetProperty("secret").GetString()!, long.Parse(request.Headers["Nytka-Timestamp"]), request.Body),
            request.Headers["Nytka-Signature"]);

        var envelope = Assert.Single(plain.Requests, r => JsonDocument.Parse(r.Body).RootElement.GetProperty("type").GetString() == "conversation.ready");
        Assert.DoesNotContain(Talk, System.Text.Encoding.UTF8.GetString(envelope.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_omi_delivery_is_retried_like_any_other()
    {
        await using var omi = await TestReceiver.StartAsync();
        omi.Status = 500;
        var client = Server.CreateAuthorizedClient();
        (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = omi.Url, events = new[] { "conversation.ready" }, format = "omi" })).EnsureSuccessStatusCode();
        await Seed(Talk, Talk);

        await TickAndRun();

        Assert.Single(omi.Requests);
        Assert.Equal("pending", await Db.ScalarAsync<string>("select status from webhook_deliveries"));
        Assert.Equal(1, await Db.ScalarAsync<int>("select attempts from webhook_deliveries"));
    }

    [Fact]
    public async Task The_format_is_checked_on_create_and_update()
    {
        var client = Server.CreateAuthorizedClient();
        var url = "http://127.0.0.1:9/hook";

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/webhooks", new { url, events = new[] { "*" }, format = "slack" })).StatusCode);
        var mixed = await client.PostAsJsonAsync("/api/v1/webhooks", new { url, events = new[] { "task.created" }, format = "omi" });
        Assert.Equal(HttpStatusCode.BadRequest, mixed.StatusCode);
        Assert.Contains("events", await mixed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var plain = await (await client.PostAsJsonAsync("/api/v1/webhooks", new { url, events = new[] { "task.created" } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("nytka", plain.GetProperty("format").GetString());
        var id = plain.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/v1/webhooks/{id}", new { format = "omi" })).StatusCode);

        var both = await client.PatchAsJsonAsync($"/api/v1/webhooks/{id}", new { format = "omi", events = new[] { "conversation.ready" } });
        Assert.Equal("omi", (await both.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("format").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/v1/webhooks/{id}", new { events = new[] { "task.created" } })).StatusCode);
    }
}
