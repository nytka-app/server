using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// A note as the API shows it: the advice of one conversation on one topic, with the conversation it came from.
/// </summary>
/// <remarks>A class, not a record: Dapper cannot pass a <c>text[]</c> to a constructor parameter of type <c>string[]</c>.</remarks>
public sealed class NoteRow
{
    public Guid Id { get; init; }

    public Guid ConversationId { get; init; }

    public string? ConversationTitle { get; init; }

    public DateTime ConversationStartedAt { get; init; }

    public string Topic { get; init; } = "";

    public string[] Points { get; init; } = [];

    public DateTime CreatedAt { get; init; }
}

/// <summary>A note the model's advice made: <paramref name="Topic"/> is a normalized name, <paramref name="Points"/> the tips.</summary>
public sealed record AiNote(string Topic, IReadOnlyList<string> Points);

/// <summary>An item of the model's answer the server did not keep (<c>dropped_candidates</c>).</summary>
public sealed record DroppedCandidate(string Kind, string Owner, string Text);

/// <summary>
/// Notes (<c>notes</c>), the advice taken from a summary and grouped by topic, and the audit of what the summary dropped
/// (<c>dropped_candidates</c>, docs/specs/task-kinds.md).
/// </summary>
public sealed class NoteStore(NpgsqlDataSource dataSource)
{
    /// <summary>Newest first, by id. <paramref name="topic"/> is normalized; <paramref name="before"/> is a note id.</summary>
    public async Task<IReadOnlyList<NoteRow>> ListAsync(string? topic, Guid? conversationId, Guid? before, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            """
            select n.id as Id, n.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as ConversationTitle,
                   c.started_at as ConversationStartedAt, n.topic as Topic, n.points as Points, n.created_at as CreatedAt
            from notes n
            join conversations c on c.id = n.conversation_id
            where (cast(@topic as text) is null or n.topic = @topic)
              and (cast(@conversationId as uuid) is null or n.conversation_id = @conversationId)
              and (cast(@before as uuid) is null or n.id < @before)
            order by n.id desc
            limit @limit
            """,
            new { topic, conversationId, before, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Replaces a conversation's notes with <paramref name="notes"/>, inside the caller's transaction: a topic that has a note
    /// takes the new points, a new topic inserts one, and a topic the summary no longer has is deleted.
    /// </summary>
    public static async Task ReplaceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, IReadOnlyList<AiNote> notes,
        DateTimeOffset now, CancellationToken ct)
    {
        var topics = notes.Select(n => n.Topic).ToArray();
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from notes where conversation_id = @conversationId and not (topic = any(@topics))",
            new { conversationId, topics }, transaction, cancellationToken: ct));

        for (var i = 0; i < notes.Count; i++)
        {
            // A millisecond apart, so ids keep the order the model gave the topics in.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into notes (id, conversation_id, topic, points, created_at, updated_at)
                values (@id, @conversationId, @Topic, @points, @now, @now)
                on conflict (conversation_id, topic) do update set points = excluded.points, updated_at = excluded.updated_at
                """,
                new { id = Guid.CreateVersion7(now.AddMilliseconds(i)), conversationId, notes[i].Topic, points = notes[i].Points.ToArray(), now },
                transaction, cancellationToken: ct));
        }
    }

    /// <summary>Replaces a conversation's audit rows with <paramref name="dropped"/>, inside the caller's transaction.</summary>
    public static async Task ReplaceDroppedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, IReadOnlyList<DroppedCandidate> dropped,
        DateTimeOffset now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from dropped_candidates where conversation_id = @conversationId", new { conversationId }, transaction, cancellationToken: ct));
        foreach (var item in dropped)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into dropped_candidates (conversation_id, kind, owner, text, created_at)
                values (@conversationId, @Kind, @Owner, @Text, @now)
                """,
                new { conversationId, item.Kind, item.Owner, item.Text, now }, transaction, cancellationToken: ct));
        }
    }
}
