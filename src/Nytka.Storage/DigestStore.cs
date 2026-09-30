using System.Text.Json;
using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A highlight of a digest; the conversation may have been deleted since.</summary>
public sealed record DigestHighlight(string Text, Guid ConversationId);

/// <summary>What a digest holds besides its headline and overview (<c>digests.body</c>).</summary>
public sealed record DigestBody(IReadOnlyList<DigestHighlight> Highlights, IReadOnlyList<string> Decisions, IReadOnlyList<string> OpenQuestions);

/// <summary><paramref name="LocalDate"/> is <c>yyyy-MM-dd</c>, the user's local day the digest covers.</summary>
public sealed record DigestRow(
    Guid Id, string LocalDate, string Headline, string Overview, IReadOnlyList<DigestHighlight> Highlights,
    IReadOnlyList<string> Decisions, IReadOnlyList<string> OpenQuestions, DateTime CreatedAt);

public sealed record DigestPage(IReadOnlyList<DigestRow> Items, string? NextBefore);

/// <summary>A conversation as the digest reads it.</summary>
public sealed record DigestConversation(Guid Id, DateTime StartedAt, string? Title, string? Summary);

/// <summary>What one local day holds, read in one place: its conversations, tasks and memories, oldest first.</summary>
public sealed record DigestInput(
    IReadOnlyList<DigestConversation> Conversations, IReadOnlyList<string> Tasks, IReadOnlyList<string> Memories);

/// <summary>Daily digests (<c>digests</c>).</summary>
public sealed class DigestStore(NpgsqlDataSource dataSource)
{
    public const int MaxConversations = 80;
    public const int MaxTasks = 100;
    public const int MaxMemories = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Select =
        "select id as Id, to_char(local_date, 'YYYY-MM-DD') as LocalDate, headline as Headline, overview as Overview, body::text as Body, created_at as CreatedAt from digests";

    private sealed record Raw(Guid Id, string LocalDate, string Headline, string Overview, string Body, DateTime CreatedAt)
    {
        public DigestRow ToRow()
        {
            var body = JsonSerializer.Deserialize<DigestBody>(Body, Json)!;
            return new DigestRow(Id, LocalDate, Headline, Overview, body.Highlights, body.Decisions, body.OpenQuestions, CreatedAt);
        }
    }

    public async Task<bool> ExistsAsync(string localDate, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from digests where local_date = cast(@localDate as date))", new { localDate }, cancellationToken: ct));
    }

    public async Task<DigestRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QuerySingleOrDefaultAsync<Raw>(new CommandDefinition(
            Select + " where id = @id", new { id }, cancellationToken: ct)))?.ToRow();
    }

    /// <summary>Newest date first. <paramref name="before"/> (<c>yyyy-MM-dd</c>) keeps earlier dates; <c>NextBefore</c> is the last date of a full page.</summary>
    public async Task<DigestPage> ListAsync(string? before, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var items = (await connection.QueryAsync<Raw>(new CommandDefinition(
            Select + " where (cast(@before as date) is null or local_date < cast(@before as date)) order by local_date desc limit @limit",
            new { before, limit }, cancellationToken: ct))).Select(r => r.ToRow()).ToList();
        return new DigestPage(items, items.Count == limit ? items[^1].LocalDate : null);
    }

    /// <summary>
    /// The conversations that started in <c>[from, to)</c> and have a title or a summary, and the live tasks and memories
    /// created in it. One connection, so the three lists describe the same moment.
    /// </summary>
    public async Task<DigestInput> ReadDayAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var args = new { from = from.ToUniversalTime(), to = to.ToUniversalTime() };
        var conversations = (await connection.QueryAsync<DigestConversation>(new CommandDefinition(
            $"""
            select id as Id, started_at as StartedAt, coalesce(title, ai_title) as Title, ai_summary as Summary
            from conversations
            where started_at >= @from and started_at < @to and (coalesce(title, ai_title) is not null or ai_summary is not null)
            order by started_at, id limit {MaxConversations}
            """,
            args, cancellationToken: ct))).ToList();
        var tasks = (await connection.QueryAsync<string>(new CommandDefinition(
            $"select text from tasks where deleted_at is null and created_at >= @from and created_at < @to order by created_at, id limit {MaxTasks}",
            args, cancellationToken: ct))).ToList();
        var memories = (await connection.QueryAsync<string>(new CommandDefinition(
            $"select text from memories where deleted_at is null and created_at >= @from and created_at < @to order by created_at, id limit {MaxMemories}",
            args, cancellationToken: ct))).ToList();
        return new DigestInput(conversations, tasks, memories);
    }

    /// <summary>
    /// Stores a digest in the caller's transaction. With <paramref name="replace"/> the date's row is deleted first;
    /// without it an existing row stays and false comes back.
    /// </summary>
    public async Task<bool> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string localDate, string headline, string overview,
        DigestBody body, bool replace, DateTimeOffset now, CancellationToken ct)
    {
        if (replace)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from digests where local_date = cast(@localDate as date)", new { localDate }, transaction, cancellationToken: ct));
        }

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into digests (id, local_date, headline, overview, body, created_at)
            values (@id, cast(@localDate as date), @headline, @overview, cast(@body as jsonb), @now)
            on conflict (local_date) do nothing
            """,
            new { id, localDate, headline, overview, body = JsonSerializer.Serialize(body, Json), now },
            transaction, cancellationToken: ct)) == 1;
    }
}
