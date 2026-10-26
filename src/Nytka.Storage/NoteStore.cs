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

    /// <summary>The model's rating, or the wearer's while <see cref="SalienceManual"/>.</summary>
    public string? Salience { get; init; }

    public bool SalienceManual { get; init; }
}

/// <summary>A note the model's advice made: <paramref name="Topic"/> is a normalized name, <paramref name="Points"/> the tips.</summary>
public sealed record AiNote(
    string Topic, IReadOnlyList<string> Points, string? Salience = null, string? Sensitivity = null, IReadOnlyList<long>? SegmentIds = null);

/// <summary>An item of the model's answer the server did not keep (<c>dropped_candidates</c>).</summary>
public sealed record DroppedCandidate(string Kind, string Owner, string Text);

/// <summary>
/// Notes (<c>notes</c>), the advice taken from a summary and grouped by topic, and the audit of what the summary dropped
/// (<c>dropped_candidates</c>, docs/specs/task-kinds.md).
/// </summary>
public sealed class NoteStore(NpgsqlDataSource dataSource)
{
    /// <summary>Newest first, by id. <paramref name="topic"/> is normalized; <paramref name="before"/> is a note id.</summary>
    public async Task<IReadOnlyList<NoteRow>> ListAsync(
        string? topic, Guid? conversationId, Guid? before, int limit, CancellationToken ct, string[]? hidden = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            $"""
            select n.id as Id, n.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as ConversationTitle,
                   c.started_at as ConversationStartedAt, n.topic as Topic, n.points as Points, n.created_at as CreatedAt,
                   n.salience as Salience, n.salience_manual as SalienceManual
            from notes n
            join conversations c on c.id = n.conversation_id
            where (cast(@topic as text) is null or n.topic = @topic)
              and (cast(@conversationId as uuid) is null or n.conversation_id = @conversationId)
              and (cast(@before as uuid) is null or n.id < @before)
              and {Sensitivity.Visible("n")} and {Sensitivity.Visible("c")}
            order by n.id desc
            limit @limit
            """,
            new { topic, conversationId, before, limit, hidden = hidden ?? [] }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>One note as the list shows it, or null when it is gone.</summary>
    public async Task<NoteRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<NoteRow>(new CommandDefinition(
            """
            select n.id as Id, n.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as ConversationTitle,
                   c.started_at as ConversationStartedAt, n.topic as Topic, n.points as Points, n.created_at as CreatedAt,
                   n.salience as Salience, n.salience_manual as SalienceManual
            from notes n join conversations c on c.id = n.conversation_id
            where n.id = @id
            """,
            new { id }, cancellationToken: ct));
    }

    /// <summary>The wearer's mark on how much a note matters, as <see cref="TaskStore.SetSalienceAsync"/> does for a task. False when it is gone.</summary>
    public async Task<bool> SetSalienceAsync(Guid id, string? salience, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            update notes set salience = coalesce(cast(@salience as text), salience), salience_manual = cast(@salience as text) is not null, updated_at = @now
            where id = @id
            """,
            new { id, salience, now }, cancellationToken: ct)) > 0;
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
            var noteId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
                """
                insert into notes (id, conversation_id, topic, points, salience, sensitivity, created_at, updated_at)
                values (@id, @conversationId, @Topic, @points, @Salience, @Sensitivity, @now, @now)
                on conflict (conversation_id, topic) do update
                set points = excluded.points, sensitivity = excluded.sensitivity, updated_at = excluded.updated_at,
                    salience = case when notes.salience_manual then notes.salience else excluded.salience end
                returning id
                """,
                new { id = Guid.CreateVersion7(now.AddMilliseconds(i)), conversationId, notes[i].Topic, points = notes[i].Points.ToArray(), notes[i].Salience, notes[i].Sensitivity, now },
                transaction, cancellationToken: ct));
            // Only lines of the note's own conversation, so an id the model made up is skipped.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                delete from note_sources where note_id = @noteId;
                insert into note_sources (note_id, segment_id)
                select @noteId, s.id from segments s where s.conversation_id = @conversationId and s.id = any(@ids)
                on conflict do nothing;
                """,
                new { noteId, conversationId, ids = (notes[i].SegmentIds ?? []).ToArray() }, transaction, cancellationToken: ct));
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
