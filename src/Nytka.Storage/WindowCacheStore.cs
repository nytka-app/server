using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// Each transcript window's LLM result, kept so a retried enrichment or memory-extraction job never pays twice for a
/// window that already succeeded (<c>llm_window_results</c>). A row is keyed by the conversation, the handler, the
/// window's position, the prompt version and a hash of what was sent for it: a changed prompt version or window text
/// is a different key, so it is never read back as a stale answer.
/// </summary>
public sealed class WindowCacheStore(NpgsqlDataSource dataSource)
{
    public async Task<string?> GetAsync(
        Guid conversationId, string handler, int windowIndex, int promptVersion, string inputHash, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            """
            select result::text from llm_window_results
            where conversation_id = @conversationId and handler = @handler and window_index = @windowIndex
              and prompt_version = @promptVersion and input_hash = @inputHash
            """,
            new { conversationId, handler, windowIndex, promptVersion, inputHash }, cancellationToken: ct));
    }

    /// <summary>Stores a window's result, replacing a stale row of the same key (an explicit re-run asked for a fresh answer); a conversation deleted during the call stores nothing.</summary>
    public async Task StoreAsync(
        Guid conversationId, string handler, int windowIndex, int promptVersion, string inputHash, string result,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into llm_window_results (conversation_id, handler, window_index, prompt_version, input_hash, result, created_at)
            select @conversationId, @handler, @windowIndex, @promptVersion, @inputHash, cast(@result as jsonb), @now
            where exists (select 1 from conversations where id = @conversationId)
            on conflict (conversation_id, handler, window_index, prompt_version, input_hash)
            do update set result = excluded.result, created_at = excluded.created_at
            """,
            new { conversationId, handler, windowIndex, promptVersion, inputHash, result, now }, cancellationToken: ct));
    }

    /// <summary>Drops every window cached for the conversation and handler: the run that follows wrote its final result.</summary>
    public async Task ClearAsync(Guid conversationId, string handler, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from llm_window_results where conversation_id = @conversationId and handler = @handler",
            new { conversationId, handler }, cancellationToken: ct));
    }

    /// <summary>Rows older than <paramref name="cutoff"/>: a conversation whose job never finished and is no longer retried.</summary>
    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from llm_window_results where created_at < @cutoff", new { cutoff }, cancellationToken: ct));
    }
}
