using Npgsql;

namespace Nytka.Server.Tests.Storage;

/// <summary>What migrations 0006 (memories) and 0008 (webhooks) add: their defaults, constraints and cascades.</summary>
[Collection(PostgresCollection.Name)]
public sealed class MemoryWebhookSchemaTests(PostgresFixture db) : IAsyncLifetime
{
    private const string UniqueViolation = "23505";
    private const string CheckViolation = "23514";
    private const string ForeignKeyViolation = "23503";

    private static readonly DateTime T0 = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record MemoryState(bool Edited, DateTime? DeletedAt, Guid? ConversationId);

    private sealed record RunState(int Failures, long? ThroughSegmentId, string? Message);

    private sealed record WebhookState(bool Active, string? Description);

    private sealed record DeliveryState(
        string Status, int Attempts, int? LastStatusCode, string? LastError, DateTime? DeliveredAt, string? Payload);

    private async Task<Guid> InsertConversation()
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at)
            values (@id, @T0, @T0, 'closed', @T0, @T0)
            """,
            new { id, T0 });
        return id;
    }

    private Task InsertMemory(string fingerprint, string source = "ai", Guid? conversation = null) =>
        db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at)
            values (@id, 'Anna is my sister', @fingerprint, @source, @conversation, @T0, @T0)
            """,
            new { id = Guid.CreateVersion7(), fingerprint, source, conversation, T0 });

    private Task InsertRun(Guid conversation, string status = "pending") =>
        db.ExecuteAsync(
            "insert into memory_runs (conversation_id, status, updated_at) values (@conversation, @status, @T0)",
            new { conversation, status, T0 });

    private async Task<Guid> InsertWebhook()
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into webhooks (id, url, secret, events, created_at, updated_at)
            values (@id, 'https://example.test/hook', 'whsec_x', array['*'], @T0, @T0)
            """,
            new { id, T0 });
        return id;
    }

    private Task InsertDelivery(Guid webhook, string status = "pending") =>
        db.ExecuteAsync(
            """
            insert into webhook_deliveries (id, webhook_id, event_id, event_type, payload, status, created_at)
            values (@id, @webhook, @eventId, 'ping', '{}'::jsonb, @status, @T0)
            """,
            new { id = Guid.CreateVersion7(), webhook, eventId = Guid.CreateVersion7(), status, T0 });

    private static async Task<string> SqlStateOf(Func<Task> statement) =>
        (await Assert.ThrowsAsync<PostgresException>(statement)).SqlState;

    [Fact]
    public async Task Adds_the_memory_and_webhook_tables()
    {
        var tables = await db.QueryAsync<string>(
            "select table_name from information_schema.tables where table_schema = 'public'");

        Assert.Superset(
            new HashSet<string> { "memories", "memory_runs", "webhooks", "webhook_deliveries" },
            tables.ToHashSet());
    }

    [Fact]
    public async Task A_new_memory_is_untouched()
    {
        var conversation = await InsertConversation();
        await InsertMemory("anna is my sister", conversation: conversation);

        var state = await db.QueryAsync<MemoryState>(
            "select edited as Edited, deleted_at as DeletedAt, conversation_id as ConversationId from memories");

        Assert.Equal(new MemoryState(false, null, conversation), Assert.Single(state));
    }

    [Fact]
    public async Task A_memory_may_have_no_source_conversation()
    {
        await InsertMemory("anna is my sister", "user");

        Assert.Null(await db.ScalarAsync<Guid?>("select conversation_id from memories"));
    }

    [Theory]
    [InlineData("ai", null)]
    [InlineData("user", null)]
    [InlineData("robot", CheckViolation)]
    public async Task A_memory_source_is_ai_or_user(string source, string? sqlState)
    {
        if (sqlState is null)
        {
            await InsertMemory("fact", source);
            Assert.Equal(source, await db.ScalarAsync<string>("select source from memories"));
        }
        else
        {
            Assert.Equal(sqlState, await SqlStateOf(() => InsertMemory("fact", source)));
        }
    }

    [Fact]
    public async Task A_memory_fingerprint_is_unique_across_conversations()
    {
        await InsertMemory("anna is my sister", conversation: await InsertConversation());
        var other = await InsertConversation();

        Assert.Equal(
            UniqueViolation,
            await SqlStateOf(() => InsertMemory("anna is my sister", conversation: other)));
    }

    [Fact]
    public async Task Memories_and_their_runs_go_with_their_conversation()
    {
        var conversation = await InsertConversation();
        await InsertMemory("one", conversation: conversation);
        await InsertMemory("two", conversation: conversation);
        await InsertMemory("kept", "user");
        await InsertRun(conversation);

        await db.ExecuteAsync("delete from conversations where id = @conversation", new { conversation });

        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from memories"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from memory_runs"));
    }

    [Fact]
    public async Task A_new_memory_run_has_no_failures_and_has_read_nothing()
    {
        await InsertRun(await InsertConversation());

        var state = await db.QueryAsync<RunState>(
            "select failures as Failures, through_segment_id as ThroughSegmentId, message as Message from memory_runs");

        Assert.Equal(new RunState(0, null, null), Assert.Single(state));
    }

    [Fact]
    public async Task A_conversation_has_one_memory_run()
    {
        var conversation = await InsertConversation();
        await InsertRun(conversation);

        Assert.Equal(UniqueViolation, await SqlStateOf(() => InsertRun(conversation)));
    }

    [Theory]
    [InlineData("pending", null)]
    [InlineData("done", null)]
    [InlineData("failed", null)]
    [InlineData("skipped", CheckViolation)]
    public async Task A_memory_run_status_is_pending_done_or_failed(string status, string? sqlState)
    {
        var conversation = await InsertConversation();

        if (sqlState is null)
        {
            await InsertRun(conversation, status);
            Assert.Equal(status, await db.ScalarAsync<string>("select status from memory_runs"));
        }
        else
        {
            Assert.Equal(sqlState, await SqlStateOf(() => InsertRun(conversation, status)));
        }
    }

    [Fact]
    public async Task A_new_webhook_is_active()
    {
        await InsertWebhook();

        var state = await db.QueryAsync<WebhookState>("select active as Active, description as Description from webhooks");

        Assert.Equal(new WebhookState(true, null), Assert.Single(state));
    }

    [Fact]
    public async Task A_new_delivery_is_pending_with_no_attempts()
    {
        await InsertDelivery(await InsertWebhook());

        var state = await db.QueryAsync<DeliveryState>(
            """
            select status as Status, attempts as Attempts, last_status_code as LastStatusCode,
                   last_error as LastError, delivered_at as DeliveredAt, payload::text as Payload
            from webhook_deliveries
            """);

        Assert.Equal(new DeliveryState("pending", 0, null, null, null, "{}"), Assert.Single(state));
    }

    [Theory]
    [InlineData("pending", null)]
    [InlineData("delivered", null)]
    [InlineData("failed", null)]
    [InlineData("retrying", CheckViolation)]
    public async Task A_delivery_status_is_pending_delivered_or_failed(string status, string? sqlState)
    {
        var webhook = await InsertWebhook();

        if (sqlState is null)
        {
            await InsertDelivery(webhook, status);
            Assert.Equal(status, await db.ScalarAsync<string>("select status from webhook_deliveries"));
        }
        else
        {
            Assert.Equal(sqlState, await SqlStateOf(() => InsertDelivery(webhook, status)));
        }
    }

    [Fact]
    public async Task A_delivery_needs_its_webhook() =>
        Assert.Equal(ForeignKeyViolation, await SqlStateOf(() => InsertDelivery(Guid.CreateVersion7())));

    [Fact]
    public async Task Deliveries_go_with_their_webhook()
    {
        var webhook = await InsertWebhook();
        var other = await InsertWebhook();
        await InsertDelivery(webhook);
        await InsertDelivery(webhook);
        await InsertDelivery(other);

        await db.ExecuteAsync("delete from webhooks where id = @webhook", new { webhook });

        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from webhook_deliveries"));
    }

    [Fact]
    public async Task Reset_empties_the_memory_and_webhook_tables()
    {
        await InsertDelivery(await InsertWebhook());
        await InsertRun(await InsertConversation());
        await InsertMemory("fact", "user");

        await db.ResetAsync();

        foreach (var table in new[] { "webhooks", "webhook_deliveries", "memories", "memory_runs" })
        {
            Assert.Equal(0, await db.ScalarAsync<long>($"select count(*) from {table}"));
        }
    }

    [Theory]
    [InlineData("memories_live", "id DESC", "WHERE (deleted_at IS NULL)")]
    [InlineData("webhook_deliveries_by_webhook", "webhook_id, created_at DESC", "created_at DESC)")]
    public async Task Indexes_the_way_the_lists_read(string index, string columns, string ending)
    {
        var definition = await db.ScalarAsync<string>("select indexdef from pg_indexes where indexname = @index", new { index });

        Assert.Contains(columns, definition, StringComparison.Ordinal);
        Assert.EndsWith(ending, definition, StringComparison.Ordinal);
    }
}
