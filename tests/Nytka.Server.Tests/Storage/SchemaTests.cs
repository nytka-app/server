using Npgsql;

namespace Nytka.Server.Tests.Storage;

/// <summary>What migrations 0003 (tokens, settings) and 0004 (AI) add: their columns, defaults and constraints.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SchemaTests(PostgresFixture db) : IAsyncLifetime
{
    private const string UniqueViolation = "23505";
    private const string CheckViolation = "23514";

    private static readonly DateTime T0 = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Column(string DataType, bool Nullable);

    private sealed record AiState(
        string Status, int Failures, string? Title, string? AiTitle, string? Summary, string? Message,
        DateTime? UpdatedAt, long? ThroughSegmentId);

    private sealed record TaskState(bool Done, DateTime? DoneAt, bool Edited, DateTime? DeletedAt);

    private async Task<Column?> ColumnOf(string table, string column)
    {
        var rows = await db.QueryAsync<Column>(
            """
            select data_type as DataType, is_nullable = 'YES' as Nullable
            from information_schema.columns
            where table_schema = 'public' and table_name = @table and column_name = @column
            """,
            new { table, column });
        return rows.SingleOrDefault();
    }

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

    private Task InsertToken(string name, string scope = "admin", byte[]? hash = null) =>
        db.ExecuteAsync(
            """
            insert into api_tokens (id, name, scope, token_hash, hint, created_at)
            values (@id, @name, @scope, @hash, 'abcd', @T0)
            """,
            new { id = Guid.CreateVersion7(), name, scope, hash = hash ?? Guid.NewGuid().ToByteArray(), T0 });

    private Task InsertTask(Guid conversation, string fingerprint) =>
        db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, created_at, updated_at)
            values (@id, @conversation, 'Call Anna', @fingerprint, @T0, @T0)
            """,
            new { id = Guid.CreateVersion7(), conversation, fingerprint, T0 });

    private static async Task<string> SqlStateOf(Func<Task> statement) =>
        (await Assert.ThrowsAsync<PostgresException>(statement)).SqlState;

    [Fact]
    public async Task Adds_the_token_settings_and_task_tables()
    {
        var tables = await db.QueryAsync<string>(
            "select table_name from information_schema.tables where table_schema = 'public'");

        Assert.Superset(new HashSet<string> { "api_tokens", "settings", "tasks" }, tables.ToHashSet());
    }

    [Theory]
    [InlineData("transcription_batches", "finished_at", "timestamp with time zone", true)]
    [InlineData("segments", "speaker", "text", true)]
    [InlineData("segments", "created_at", "timestamp with time zone", false)]
    [InlineData("conversations", "title", "text", true)]
    [InlineData("conversations", "ai_title", "text", true)]
    [InlineData("conversations", "ai_summary", "text", true)]
    [InlineData("conversations", "ai_status", "text", false)]
    [InlineData("conversations", "ai_message", "text", true)]
    [InlineData("conversations", "ai_updated_at", "timestamp with time zone", true)]
    [InlineData("conversations", "ai_through_segment_id", "bigint", true)]
    [InlineData("conversations", "ai_failures", "integer", false)]
    public async Task Adds_the_ai_columns(string table, string column, string dataType, bool nullable) =>
        Assert.Equal(new Column(dataType, nullable), await ColumnOf(table, column));

    [Fact]
    public async Task A_new_conversation_has_no_ai_work_yet()
    {
        var id = await InsertConversation();

        var state = await db.QueryAsync<AiState>(
            """
            select ai_status as Status, ai_failures as Failures, title as Title, ai_title as AiTitle,
                   ai_summary as Summary, ai_message as Message, ai_updated_at as UpdatedAt,
                   ai_through_segment_id as ThroughSegmentId
            from conversations where id = @id
            """,
            new { id });

        Assert.Equal(new AiState("none", 0, null, null, null, null, null, null), Assert.Single(state));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("pending")]
    [InlineData("done")]
    [InlineData("skipped")]
    [InlineData("failed")]
    public async Task Accepts_every_ai_status(string status)
    {
        var id = await InsertConversation();

        await db.ExecuteAsync("update conversations set ai_status = @status where id = @id", new { id, status });

        Assert.Equal(status, await db.ScalarAsync<string>("select ai_status from conversations where id = @id", new { id }));
    }

    [Fact]
    public async Task Refuses_an_unknown_ai_status()
    {
        var id = await InsertConversation();

        var state = await SqlStateOf(() =>
            db.ExecuteAsync("update conversations set ai_status = 'nearly' where id = @id", new { id }));

        Assert.Equal(CheckViolation, state);
    }

    [Fact]
    public async Task A_stored_segment_carries_its_storing_time_and_no_speaker()
    {
        var conversation = await InsertConversation();
        var batch = await db.ScalarAsync<long>(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at)
            values (@conversation, @T0, @T0, 'done', '{}', @T0)
            returning id
            """,
            new { conversation, T0 });

        await db.ExecuteAsync(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text)
            values (@conversation, @batch, @T0, @T0, 'hello')
            """,
            new { conversation, batch, T0 });

        Assert.True(await db.ScalarAsync<bool>(
            "select created_at <= now() and created_at > now() - interval '1 minute' and speaker is null from segments"));
    }

    [Fact]
    public async Task A_token_name_is_unique_among_active_tokens_ignoring_case()
    {
        await InsertToken("Laptop");

        Assert.Equal(UniqueViolation, await SqlStateOf(() => InsertToken("laptop")));
    }

    [Fact]
    public async Task Revoking_a_token_frees_its_name()
    {
        await InsertToken("Laptop");
        await db.ExecuteAsync("update api_tokens set revoked_at = @T0", new { T0 });

        await InsertToken("laptop");

        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from api_tokens"));
        Assert.Equal(UniqueViolation, await SqlStateOf(() => InsertToken("LAPTOP")));
    }

    [Fact]
    public async Task A_token_hash_is_unique()
    {
        var hash = new byte[] { 1, 2, 3 };
        await InsertToken("one", hash: hash);

        Assert.Equal(UniqueViolation, await SqlStateOf(() => InsertToken("two", hash: hash)));
    }

    [Theory]
    [InlineData("admin", null)]
    [InlineData("read", null)]
    [InlineData("write", CheckViolation)]
    public async Task A_token_scope_is_admin_or_read(string scope, string? sqlState)
    {
        if (sqlState is null)
        {
            await InsertToken("name", scope);
            Assert.Equal(scope, await db.ScalarAsync<string>("select scope from api_tokens"));
        }
        else
        {
            Assert.Equal(sqlState, await SqlStateOf(() => InsertToken("name", scope)));
        }
    }

    [Fact]
    public async Task A_setting_key_is_unique()
    {
        const string sql = "insert into settings (key, value, updated_at) values ('llm.model', 'a', @T0)";
        await db.ExecuteAsync(sql, new { T0 });

        Assert.Equal(UniqueViolation, await SqlStateOf(() => db.ExecuteAsync(sql, new { T0 })));
    }

    [Fact]
    public async Task A_new_task_is_open_and_untouched()
    {
        await InsertTask(await InsertConversation(), "call anna");

        var state = await db.QueryAsync<TaskState>(
            "select done as Done, done_at as DoneAt, edited as Edited, deleted_at as DeletedAt from tasks");

        Assert.Equal(new TaskState(false, null, false, null), Assert.Single(state));
    }

    [Fact]
    public async Task A_task_fingerprint_is_unique_within_its_conversation()
    {
        var conversation = await InsertConversation();
        await InsertTask(conversation, "call anna");

        await InsertTask(await InsertConversation(), "call anna");

        Assert.Equal(UniqueViolation, await SqlStateOf(() => InsertTask(conversation, "call anna")));
    }

    [Fact]
    public async Task Tasks_go_with_their_conversation()
    {
        var conversation = await InsertConversation();
        await InsertTask(conversation, "call anna");
        await InsertTask(conversation, "buy milk");

        await db.ExecuteAsync("delete from conversations where id = @conversation", new { conversation });

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from tasks"));
    }

    [Theory]
    [InlineData("api_tokens_active_name", "lower(name)", "WHERE (revoked_at IS NULL)")]
    [InlineData("tasks_live", "id DESC", "WHERE (deleted_at IS NULL)")]
    public async Task Indexes_only_the_live_rows(string index, string column, string predicate)
    {
        var definition = await db.ScalarAsync<string>("select indexdef from pg_indexes where indexname = @index", new { index });

        Assert.Contains(column, definition, StringComparison.Ordinal);
        Assert.EndsWith(predicate, definition, StringComparison.Ordinal);
    }
}
