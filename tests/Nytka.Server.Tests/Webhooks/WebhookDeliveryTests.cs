using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Webhooks;
using Nytka.Storage;

namespace Nytka.Server.Tests.Webhooks;

[Collection(PostgresCollection.Name)]
public sealed class WebhookDeliveryTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    private readonly NytkaApiFactory _server = new(db);
    private TestReceiver _receiver = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _receiver = await TestReceiver.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _server.Dispose();
        await _receiver.DisposeAsync();
    }

    private sealed record DeliveryState(
        string Status, int Attempts, int? LastStatusCode, string? LastError, DateTime? DeliveredAt, string? Payload);

    private async Task<(Guid Id, string Secret)> CreateWebhook(string[]? events = null, string? url = null)
    {
        var created = await (await _server.CreateAuthorizedClient().PostAsJsonAsync(
                "/api/v1/webhooks", new { url = url ?? _receiver.Url, events = events ?? ["*"] }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return (created.GetProperty("id").GetGuid(), created.GetProperty("secret").GetString()!);
    }

    private async Task Publish(string type, Guid subject)
    {
        await using var connection = await _server.Get<NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await _server.Get<IEventPublisher>().PublishAsync(new NytkaEvent(type, subject), connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private async Task<Guid> SeedConversation()
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at, ai_title, ai_summary, ai_status)
            values (@id, @T0, @T1, 'closed', @T0, @T0, 'Trip planning', 'We planned a trip.', 'done')
            """,
            new { id, T0, T1 = T0.AddMinutes(5) });
        await db.ExecuteAsync(
            """
            with b as (
                insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at)
                values (@id, @T0, @T0, 'done', '[]', @T0) returning id)
            insert into segments (conversation_id, batch_id, started_at, ended_at, text)
            select @id, id, @T0, @T0, 'SECRET TRANSCRIPT WORDS' from b
            """,
            new { id, T0 });
        return id;
    }

    private async Task<Guid> SeedTask(Guid conversation, string text = "Book the hotel", bool deleted = false)
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, created_at, updated_at, deleted_at)
            values (@id, @conversation, @text, @text, @T0, @T0, case when @deleted then @T0 end)
            """,
            new { id, conversation, text, T0, deleted });
        return id;
    }

    private Task<DeliveryState> State() => db.QueryAsync<DeliveryState>(
        """
        select status as Status, attempts as Attempts, last_status_code as LastStatusCode, last_error as LastError,
               delivered_at as DeliveredAt, payload::text as Payload
        from webhook_deliveries
        """).ContinueWith(t => t.Result.Single());

    [Fact]
    public async Task A_2xx_delivers_a_signed_post_and_keeps_no_payload()
    {
        var (_, secret) = await CreateWebhook();
        var conversation = await SeedConversation();
        var task = await SeedTask(conversation);
        await SeedTask(conversation, "Deleted one", deleted: true);

        await Publish(NytkaEvent.ConversationReady, conversation);
        await _server.RunJobsAsync();

        var request = Assert.Single(_receiver.Requests);
        Assert.Equal("application/json", request.Headers["Content-Type"]);
        Assert.Equal("conversation.ready", request.Headers["Nytka-Event"]);
        var timestamp = long.Parse(request.Headers["Nytka-Timestamp"]);
        Assert.Equal(_server.Time.GetUtcNow().ToUnixTimeSeconds(), timestamp);
        Assert.Equal(WebhookSigner.Sign(secret, timestamp, request.Body), request.Headers["Nytka-Signature"]);

        var payload = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("conversation.ready", payload.GetProperty("type").GetString());
        Assert.Equal("2026-09-29T10:00:00Z", payload.GetProperty("createdAt").GetString());
        var data = payload.GetProperty("data");
        Assert.Equal(conversation, data.GetProperty("id").GetGuid());
        Assert.Equal("Trip planning", data.GetProperty("title").GetString());
        Assert.Equal("We planned a trip.", data.GetProperty("summary").GetString());
        var tasks = data.GetProperty("tasks");
        Assert.Equal(1, tasks.GetArrayLength());
        Assert.Equal(task, tasks[0].GetProperty("id").GetGuid());
        Assert.DoesNotContain("SECRET TRANSCRIPT", Encoding.UTF8.GetString(request.Body), StringComparison.Ordinal);
        Assert.Equal(payload.GetProperty("id").GetGuid().ToString(), await db.ScalarAsync<string>("select event_id::text from webhook_deliveries"));
        Assert.Equal(request.Headers["Nytka-Delivery"], await db.ScalarAsync<string>("select id::text from webhook_deliveries"));

        var state = await State();
        Assert.Equal(("delivered", 1, 200, null), (state.Status, state.Attempts, state.LastStatusCode, state.LastError));
        Assert.NotNull(state.DeliveredAt);
        Assert.Null(state.Payload);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs"));
    }

    [Fact]
    public async Task Task_and_memory_events_carry_their_own_data()
    {
        await CreateWebhook();
        var conversation = await SeedConversation();
        var task = await SeedTask(conversation);
        var memory = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at)
            values (@memory, 'Anna is my sister', 'fp', 'ai', @conversation, @T0, @T0)
            """,
            new { memory, conversation, T0 });

        await Publish(NytkaEvent.TaskCreated, task);
        await Publish(NytkaEvent.TaskCompleted, task);
        await Publish(NytkaEvent.MemoryCreated, memory);
        await _server.RunJobsAsync();

        var bodies = _receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement).ToDictionary(b => b.GetProperty("type").GetString()!);
        Assert.Equal(3, bodies.Count);
        var taskData = bodies["task.created"].GetProperty("data");
        Assert.Equal("Book the hotel", taskData.GetProperty("text").GetString());
        Assert.Equal("Trip planning", taskData.GetProperty("conversationTitle").GetString());
        Assert.False(taskData.GetProperty("done").GetBoolean());
        var memoryData = bodies["memory.created"].GetProperty("data");
        Assert.Equal("Anna is my sister", memoryData.GetProperty("text").GetString());
        Assert.Equal(conversation, memoryData.GetProperty("conversationId").GetGuid());
        foreach (var request in _receiver.Requests)
        {
            Assert.DoesNotContain("SECRET TRANSCRIPT", Encoding.UTF8.GetString(request.Body), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_event_makes_one_delivery_per_active_matching_webhook_only()
    {
        var (matching, _) = await CreateWebhook(["task.created"]);
        await CreateWebhook(["*"]);
        await CreateWebhook(["memory.created"]);
        var (inactive, _) = await CreateWebhook(["task.created"]);
        await _server.CreateAuthorizedClient().PatchAsJsonAsync($"/api/v1/webhooks/{inactive}", new { active = false });
        var conversation = await SeedConversation();
        var task = await SeedTask(conversation);

        await Publish(NytkaEvent.TaskCreated, task);

        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from webhook_deliveries"));
        Assert.Equal(2, await db.ScalarAsync<long>("select count(distinct webhook_id) from webhook_deliveries"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(distinct event_id) from webhook_deliveries"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from webhook_deliveries where webhook_id = @matching", new { matching }));
        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'deliver-webhook'"));
    }

    [Fact]
    public async Task A_rolled_back_change_leaves_no_delivery_and_no_job()
    {
        await CreateWebhook();
        var conversation = await SeedConversation();

        await using (var connection = await _server.Get<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await _server.Get<IEventPublisher>().PublishAsync(
                new NytkaEvent(NytkaEvent.ConversationReady, conversation), connection, transaction, CancellationToken.None);
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from webhook_deliveries"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs"));
    }

    [Fact]
    public async Task A_second_delivery_for_the_same_event_and_webhook_is_ignored()
    {
        var (webhook, _) = await CreateWebhook();
        var store = _server.Get<WebhookStore>();
        var eventId = Guid.NewGuid();

        await using var connection = await _server.Get<NpgsqlDataSource>().OpenConnectionAsync();
        Assert.True(await store.InsertDeliveryAsync(connection, null, Guid.NewGuid(), webhook, eventId, "task.created", "{}", _server.Time.GetUtcNow(), default));
        Assert.False(await store.InsertDeliveryAsync(connection, null, Guid.NewGuid(), webhook, eventId, "task.created", "{}", _server.Time.GetUtcNow(), default));

        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from webhook_deliveries"));
    }

    [Fact]
    public async Task A_failing_receiver_sees_retries_on_the_schedule_and_the_delivery_ends_failed()
    {
        _receiver.Status = 500;
        await CreateWebhook();
        var conversation = await SeedConversation();
        await Publish(NytkaEvent.ConversationReady, conversation);

        var waits = new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12) };
        Assert.Equal(1, await _server.RunJobsAsync());
        for (var i = 0; i < waits.Length; i++)
        {
            var state = await State();
            Assert.Equal(("pending", i + 1, 500, "HTTP 500"), (state.Status, state.Attempts, state.LastStatusCode, state.LastError));
            Assert.NotNull(state.Payload);

            _server.Time.Advance(waits[i] - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await _server.RunJobsAsync());
            _server.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(1, await _server.RunJobsAsync());
        }

        var last = await State();
        Assert.Equal(("failed", 6, 500, "HTTP 500"), (last.Status, last.Attempts, last.LastStatusCode, last.LastError));
        Assert.Null(last.Payload);
        Assert.Equal(6, _receiver.Requests.Count);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs"));
        _server.Time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(0, await _server.RunJobsAsync());
        Assert.Equal(6, _receiver.Requests.Count);
    }

    [Fact]
    public async Task A_2xx_on_a_retry_stops_the_retries()
    {
        _receiver.Status = 503;
        await CreateWebhook();
        await Publish(NytkaEvent.ConversationReady, await SeedConversation());
        await _server.RunJobsAsync();

        _receiver.Status = 204;
        _server.Time.Advance(TimeSpan.FromMinutes(1));
        await _server.RunJobsAsync();

        var state = await State();
        Assert.Equal(("delivered", 2, 204), (state.Status, state.Attempts, state.LastStatusCode));
        Assert.Null(state.LastError);
        Assert.Null(state.Payload);
        _server.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await _server.RunJobsAsync());
    }

    [Fact]
    public async Task A_redirect_is_not_followed_and_counts_as_a_failure()
    {
        _receiver.Status = 302;
        await CreateWebhook();
        await Publish(NytkaEvent.ConversationReady, await SeedConversation());

        await _server.RunJobsAsync();

        var state = await State();
        Assert.Equal(("pending", 302, "HTTP 302"), (state.Status, state.LastStatusCode ?? 0, state.LastError));
        Assert.Single(_receiver.Requests);
    }

    [Fact]
    public async Task A_refused_connection_is_recorded_without_the_exception_message()
    {
        var (_, _) = await CreateWebhook(url: "http://127.0.0.1:1/hook");
        await Publish(NytkaEvent.ConversationReady, await SeedConversation());

        await _server.RunJobsAsync();

        var state = await State();
        Assert.Equal(("pending", 1, null, "connection refused"), (state.Status, state.Attempts, state.LastStatusCode, state.LastError));
    }

    [Fact]
    public async Task A_timeout_is_recorded_as_timeout()
    {
        await using var slow = new NytkaApiFactory(db, services: s =>
            s.AddHttpClient(WebhooksExtensions.ClientName).ConfigurePrimaryHttpMessageHandler(() => new HangingHandler()));
        var created = await (await slow.CreateAuthorizedClient().PostAsJsonAsync(
            "/api/v1/webhooks", new { url = "http://10.255.255.1/hook", events = new[] { "*" } })).Content.ReadFromJsonAsync<JsonElement>();
        await slow.CreateAuthorizedClient().PostAsync($"/api/v1/webhooks/{created.GetProperty("id").GetGuid()}/test", null);

        Assert.Equal(1, await slow.RunJobsAsync());

        var state = await State();
        Assert.Equal(("pending", 1, null, "timeout"), (state.Status, state.Attempts, state.LastStatusCode, state.LastError));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // HttpClient.Timeout cancels the token after 10 s; a real wait would slow the suite.
            await Task.Yield();
            throw new TaskCanceledException("timed out", new TimeoutException(), cancellationToken);
        }
    }

    [Fact]
    public async Task Test_delivers_a_ping_with_an_empty_data_object_even_while_inactive()
    {
        var (id, secret) = await CreateWebhook();
        var client = _server.CreateAuthorizedClient();
        await client.PatchAsJsonAsync($"/api/v1/webhooks/{id}", new { active = false });

        await client.PostAsync($"/api/v1/webhooks/{id}/test", null);
        await _server.RunJobsAsync();

        var request = Assert.Single(_receiver.Requests);
        Assert.Equal("ping", request.Headers["Nytka-Event"]);
        var payload = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("ping", payload.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Object, payload.GetProperty("data").ValueKind);
        Assert.Empty(payload.GetProperty("data").EnumerateObject());
        Assert.Equal(WebhookSigner.Sign(secret, long.Parse(request.Headers["Nytka-Timestamp"]), request.Body), request.Headers["Nytka-Signature"]);

        var listed = await client.GetFromJsonAsync<JsonElement>($"/api/v1/webhooks/{id}/deliveries");
        Assert.Equal("delivered", listed.GetProperty("items")[0].GetProperty("status").GetString());
        var webhook = (await client.GetFromJsonAsync<JsonElement>("/api/v1/webhooks")).GetProperty("items")[0];
        Assert.Equal("delivered", webhook.GetProperty("lastDelivery").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_webhook_gets_nothing_from_before_it_existed()
    {
        var conversation = await SeedConversation();
        await Publish(NytkaEvent.ConversationReady, conversation);
        await CreateWebhook();

        Assert.Equal(0, await _server.RunJobsAsync());
        Assert.Empty(_receiver.Requests);
    }

    [Fact]
    public async Task Deliveries_run_in_the_Hooks_lane_with_a_ten_second_timeout()
    {
        Assert.Equal(JobLane.Hooks, JobKinds.LaneOf(JobKinds.DeliverWebhook));
        Assert.NotEqual(JobLane.Hooks, JobKinds.LaneOf(JobKinds.Transcribe));
        Assert.NotEqual(JobLane.Hooks, JobKinds.LaneOf(JobKinds.ExtractMemories));
        Assert.Equal(TimeSpan.FromSeconds(10), DeliverWebhookHandler.Timeout);
    }

    [Fact]
    public async Task Publishing_the_same_event_twice_makes_one_delivery()
    {
        await CreateWebhook();
        var conversation = await SeedConversation();
        var task = await SeedTask(conversation);

        await Publish(NytkaEvent.TaskCreated, task);
        await Publish(NytkaEvent.TaskCreated, task);

        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from webhook_deliveries"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from jobs"));
    }

    [Fact]
    public async Task The_trim_never_deletes_a_pending_delivery()
    {
        var (id, _) = await CreateWebhook();
        var store = _server.Get<WebhookStore>();
        await using var connection = await _server.Get<NpgsqlDataSource>().OpenConnectionAsync();
        for (var i = 0; i < WebhookStore.KeptDeliveries + 5; i++)
        {
            await store.InsertDeliveryAsync(
                connection, null, Guid.CreateVersion7(), id, Guid.NewGuid(), "ping", "{}", _server.Time.GetUtcNow().AddSeconds(i), default);
        }

        Assert.Equal(WebhookStore.KeptDeliveries + 5, await db.ScalarAsync<long>("select count(*) from webhook_deliveries"));
        await db.ExecuteAsync("update webhook_deliveries set status = 'delivered', payload = null");
        await store.InsertDeliveryAsync(connection, null, Guid.CreateVersion7(), id, Guid.NewGuid(), "ping", "{}", _server.Time.GetUtcNow().AddDays(1), default);

        Assert.Equal(WebhookStore.KeptDeliveries, await db.ScalarAsync<long>("select count(*) from webhook_deliveries"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from webhook_deliveries where status = 'pending'"));
    }

    [Fact]
    public async Task Only_the_newest_200_deliveries_are_kept()
    {
        var (id, _) = await CreateWebhook();
        var store = _server.Get<WebhookStore>();
        await using var connection = await _server.Get<NpgsqlDataSource>().OpenConnectionAsync();
        var first = Guid.NewGuid();
        for (var i = 0; i < WebhookStore.KeptDeliveries + 5; i++)
        {
            await store.InsertDeliveryAsync(
                connection, null, i == 0 ? first : Guid.CreateVersion7(), id, Guid.NewGuid(), "ping", "{}",
                _server.Time.GetUtcNow().AddSeconds(i), default);
            await db.ExecuteAsync("update webhook_deliveries set status = 'delivered', payload = null");
        }

        Assert.Equal(WebhookStore.KeptDeliveries, await db.ScalarAsync<long>("select count(*) from webhook_deliveries"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from webhook_deliveries where id = @first", new { first }));
    }
}
