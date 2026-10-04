using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A task with the conversation it came from, as the API shows it.</summary>
public sealed record TaskRow(
    Guid Id, Guid ConversationId, string? ConversationTitle, DateTime ConversationStartedAt, string Text,
    bool Done, DateTime? DoneAt, DateTime CreatedAt, Guid? PersonId, string? PersonName);

/// <summary>
/// A task the model returned: its text as stored, the fingerprint the text had when it was created and the person it is
/// owed to, if the model named a known one.
/// </summary>
public sealed record AiTask(string Text, string Fingerprint, Guid? PersonId = null);

/// <summary>A user's change of a task's person: <see langword="null"/> clears it.</summary>
public sealed record PersonChange(Guid? PersonId);

/// <summary>What <see cref="TaskStore.UpdateAsync"/> did.</summary>
public sealed record TaskUpdate(TaskRow Task, bool Completed);

/// <summary>Tasks taken from a summary (<c>tasks</c>).</summary>
public sealed class TaskStore(NpgsqlDataSource dataSource)
{
    private sealed record Current(string Text, bool Done, Guid? PersonId);

    private sealed record Existing(Guid Id, string Fingerprint, bool Done, bool Edited, DateTime? DeletedAt);

    private const string Select =
        """
        select t.id as Id, t.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as ConversationTitle,
               c.started_at as ConversationStartedAt, t.text as Text, t.done as Done, t.done_at as DoneAt,
               t.created_at as CreatedAt, t.person_id as PersonId, p.name as PersonName
        from tasks t
        join conversations c on c.id = t.conversation_id
        left join people p on p.id = t.person_id
        """;

    /// <summary>Newest first, by id. Deleted tasks never show. <paramref name="before"/> is a task id.</summary>
    public async Task<IReadOnlyList<TaskRow>> ListAsync(
        bool done, Guid? conversationId, Guid? before, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<TaskRow>(new CommandDefinition(
            Select +
            """

            where t.deleted_at is null and t.done = @done
              and (cast(@conversationId as uuid) is null or t.conversation_id = @conversationId)
              and (cast(@before as uuid) is null or t.id < @before)
            order by t.id desc
            limit @limit
            """,
            new { done, conversationId, before, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>A conversation's tasks in creation order, deleted ones left out.</summary>
    public async Task<IReadOnlyList<TaskRow>> ForConversationAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<TaskRow>(new CommandDefinition(
            Select + "\nwhere t.conversation_id = @conversationId and t.deleted_at is null order by t.id",
            new { conversationId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Applies a user's edit inside the caller's transaction and returns the task, or null when it
    /// does not exist or is deleted. <paramref name="text"/> and <paramref name="done"/> are null
    /// when the request left them out, and so is <paramref name="person"/>. An edit of the text marks the
    /// task <c>edited</c>, and so does reopening it or changing its person: a task the user touched is
    /// never removed by a later summary. A person that does not exist throws a foreign key violation.
    /// </summary>
    public async Task<TaskUpdate?> UpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string? text, bool? done,
        PersonChange? person, DateTimeOffset now, CancellationToken ct)
    {
        var current = await connection.QuerySingleOrDefaultAsync<Current>(new CommandDefinition(
            "select text as Text, done as Done, person_id as PersonId from tasks where id = @id and deleted_at is null for update",
            new { id }, transaction, cancellationToken: ct));
        if (current is not { } before)
        {
            return null;
        }

        var textChanged = text is not null && text != before.Text;
        var completed = done == true && !before.Done;
        var reopened = done == false && before.Done;
        var personChanged = person is not null && person.PersonId != before.PersonId;
        if (textChanged || completed || reopened || personChanged)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                update tasks
                set text = @text,
                    done = @done,
                    done_at = case when @completed then @now when @reopened then null else done_at end,
                    person_id = case when @personChanged then @personId else person_id end,
                    edited = edited or @textChanged or @reopened or @personChanged,
                    updated_at = @now
                where id = @id
                """,
                new
                {
                    id, text = textChanged ? text : before.Text, done = done ?? before.Done,
                    completed, reopened, textChanged, personChanged, personId = person?.PersonId, now,
                },
                transaction, cancellationToken: ct));
        }

        var task = await connection.QuerySingleAsync<TaskRow>(new CommandDefinition(
            Select + "\nwhere t.id = @id", new { id }, transaction, cancellationToken: ct));
        return new TaskUpdate(task, completed);
    }

    /// <summary>Marks the task deleted and keeps the row as a tombstone, so a later summary does not bring it back.</summary>
    public async Task<bool> DeleteAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "update tasks set deleted_at = @now, updated_at = @now where id = @id and deleted_at is null",
            new { id, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Brings a conversation's tasks in line with what the model returned, inside the caller's
    /// transaction. A returned fingerprint that has a row, in any state, changes nothing; a new one
    /// inserts a task; an untouched row (open, not edited, not deleted) the model no longer returns
    /// is deleted. Returns the ids of the inserted tasks.
    /// <para>
    /// A task the model words differently (a new fingerprint) is inserted as a new task and publishes
    /// <c>task.created</c> again, while its old row is deleted or, if the user touched it, stays. The
    /// spec allows this; v0.4's webhooks will see the second event.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ReconcileAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, IReadOnlyList<AiTask> returned,
        DateTimeOffset now, CancellationToken ct)
    {
        var existing = (await connection.QueryAsync<Existing>(
            new CommandDefinition(
                """
                select id as Id, fingerprint as Fingerprint, done as Done, edited as Edited, deleted_at as DeletedAt
                from tasks where conversation_id = @conversationId for update
                """,
                new { conversationId }, transaction, cancellationToken: ct))).ToList();

        var known = existing.Select(t => t.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var kept = returned.Select(t => t.Fingerprint).ToHashSet(StringComparer.Ordinal);

        var stale = existing
            .Where(t => t is { Done: false, Edited: false, DeletedAt: null } && !kept.Contains(t.Fingerprint))
            .Select(t => t.Id)
            .ToArray();
        if (stale.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from tasks where id = any(@stale)", new { stale }, transaction, cancellationToken: ct));
        }

        var inserted = new List<Guid>();
        foreach (var task in returned)
        {
            if (!known.Add(task.Fingerprint))
            {
                continue;
            }

            // A millisecond apart, so ids keep the order the model gave the tasks in.
            var id = Guid.CreateVersion7(now.AddMilliseconds(inserted.Count));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into tasks (id, conversation_id, text, fingerprint, person_id, created_at, updated_at)
                values (@id, @conversationId, @Text, @Fingerprint, (select id from people where id = @PersonId), @now, @now)
                """,
                new { id, conversationId, task.Text, task.Fingerprint, task.PersonId, now }, transaction, cancellationToken: ct));
            inserted.Add(id);
        }

        return inserted;
    }
}
