using Dapper;
using Npgsql;

namespace Nytka.Server.Tests.Search;

/// <summary>Rows for the search tests, written straight into the schema (the code that writes them never changes).</summary>
public static class SearchSeed
{
    public static readonly DateTime T0 = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    public static async Task<Guid> ConversationAsync(
        NpgsqlDataSource db, DateTime start, string? title = null, string? aiTitle = null, string? summary = null,
        params string[] segments)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, title, ai_title, ai_summary, created_at, updated_at)
            values (@id, @start, @start + interval '1 minute', 'closed', @title, @aiTitle, @summary, @start, @start)
            """,
            new { id, start, title, aiTitle, summary });
        var batch = await connection.ExecuteScalarAsync<long>(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at)
            values (@id, @start, @start, 'done', '[]', @start) returning id
            """,
            new { id, start });
        for (var i = 0; i < segments.Length; i++)
        {
            await connection.ExecuteAsync(
                """
                insert into segments (conversation_id, batch_id, started_at, ended_at, text)
                values (@id, @batch, @at, @at, @text)
                """,
                new { id, batch, at = start.AddSeconds(i), text = segments[i] });
        }

        return id;
    }

    public static async Task<Guid> MemoryAsync(
        NpgsqlDataSource db, string text, Guid? conversationId = null, bool deleted = false)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await db.OpenConnectionAsync();
        await connection.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, conversation_id, deleted_at, created_at, updated_at)
            values (@id, @text, @id::text, 'ai', @conversationId, case when @deleted then @now end, @now, @now)
            """,
            new { id, text, conversationId, deleted, now = T0 });
        return id;
    }

    public static async Task ClearAsync(NpgsqlDataSource db)
    {
        await using var connection = await db.OpenConnectionAsync();
        await connection.ExecuteAsync("truncate conversations, memories, capture_sessions cascade");
    }
}
