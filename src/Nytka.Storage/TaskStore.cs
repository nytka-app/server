using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A task with the conversation it came from, as the API shows it.</summary>
public sealed record TaskRow(
    Guid Id, Guid? ConversationId, string? ConversationTitle, DateTime? ConversationStartedAt, string Text,
    bool Done, DateTime? DoneAt, DateTime CreatedAt, Guid? PersonId, string? PersonName, string Kind, DateTime? Due);

/// <summary>
/// A task the model returned: its text as stored, the fingerprint the text had when it was created, the person it is
/// owed to (for a <c>waiting_on</c> task, who owes it), if the model named a known one, and its <see cref="TaskKinds">kind</see>
/// (a commitment, an idea or something awaited), and its due instant when the speaker named one.
/// </summary>
public sealed record AiTask(string Text, string Fingerprint, Guid? PersonId = null, string Kind = TaskKinds.Commitment, DateTime? Due = null);

/// <summary>A user's change of a task's person: <see langword="null"/> clears it.</summary>
public sealed record PersonChange(Guid? PersonId);

/// <summary>A user's change of a task's due date: <see langword="null"/> clears it.</summary>
public sealed record DueChange(DateTime? Due);

/// <summary>What <see cref="TaskStore.UpdateAsync"/> did.</summary>
public sealed record TaskUpdate(TaskRow Task, bool Completed);

/// <summary>What stopped <see cref="TaskStore.CreateAsync"/> from adding the task, if anything.</summary>
public enum TaskCreateProblem { None, NoConversation, NoPerson, Duplicate }

/// <summary>What <see cref="TaskStore.CreateAsync"/> did: the task, or which <see cref="TaskCreateProblem"/> it hit.</summary>
public sealed record TaskCreated(TaskRow? Task, TaskCreateProblem Problem);

/// <summary>Tasks taken from a summary (<c>tasks</c>).</summary>
public sealed class TaskStore(NpgsqlDataSource dataSource)
{
    private sealed record Current(string Text, bool Done, Guid? PersonId, DateTime? Due);

    private sealed record Existing(Guid Id, string Fingerprint, bool Done, bool Edited, DateTime? DeletedAt, string Kind, DateTime? Due);

    private const string Select =
        """
        select t.id as Id, t.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as ConversationTitle,
               c.started_at as ConversationStartedAt, t.text as Text, t.done as Done, t.done_at as DoneAt,
               t.created_at as CreatedAt, t.person_id as PersonId, p.name as PersonName, t.kind as Kind,
               t.due_at as Due
        from tasks t
        left join conversations c on c.id = t.conversation_id
        left join people p on p.id = t.person_id
        """;

    /// <summary>
    /// Newest first, by id. Deleted tasks never show. <paramref name="before"/> is a task id; <paramref name="kind"/> is a
    /// stored kind, or null for every kind.
    /// </summary>
    public async Task<IReadOnlyList<TaskRow>> ListAsync(
        bool done, Guid? conversationId, Guid? before, string? kind, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<TaskRow>(new CommandDefinition(
            Select +
            """

            where t.deleted_at is null and t.done = @done
              and (cast(@conversationId as uuid) is null or t.conversation_id = @conversationId)
              and (cast(@before as uuid) is null or t.id < @before)
              and (cast(@kind as text) is null or t.kind = @kind)
            order by t.id desc
            limit @limit
            """,
            new { done, conversationId, before, kind, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>The person's open commitments, newest first, deleted ones left out.</summary>
    public async Task<IReadOnlyList<TaskRow>> OpenForPersonAsync(Guid personId, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<TaskRow>(new CommandDefinition(
            Select + "\nwhere t.person_id = @personId and t.done = false and t.deleted_at is null and t.kind = 'commitment' order by t.id desc limit @limit",
            new { personId, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>What the person owes the wearer: their open <c>waiting_on</c> tasks, newest first, deleted ones left out.</summary>
    public async Task<IReadOnlyList<TaskRow>> OpenWaitingOnForPersonAsync(Guid personId, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<TaskRow>(new CommandDefinition(
            Select + "\nwhere t.person_id = @personId and t.done = false and t.deleted_at is null and t.kind = 'waiting_on' order by t.id desc limit @limit",
            new { personId, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// A person's tasks of one <paramref name="kind"/>, open or done, deleted ones left out: newest first, or the most recently
    /// done first for done ones (docs/specs/promise-ledger.md).
    /// </summary>
    public async Task<IReadOnlyList<TaskRow>> ForPersonAsync(Guid personId, string kind, bool done, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<TaskRow>(new CommandDefinition(
            Select + "\nwhere t.person_id = @personId and t.done = @done and t.deleted_at is null and t.kind = @kind " +
            "order by case when @done then t.done_at end desc nulls last, t.id desc limit @limit",
            new { personId, kind, done, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Open commitments with a due date, soonest due first: the <c>calendar.ics</c> feed's <c>VTODO</c>s.</summary>
    public async Task<IReadOnlyList<TaskRow>> OpenWithDueAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<TaskRow>(new CommandDefinition(
            Select + "\nwhere t.kind = @kind and t.done = false and t.deleted_at is null and t.due_at is not null order by t.due_at, t.id",
            new { kind = TaskKinds.Commitment }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>A conversation's commitments in creation order, deleted ones left out.</summary>
    public async Task<IReadOnlyList<TaskRow>> ForConversationAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<TaskRow>(new CommandDefinition(
            Select + "\nwhere t.conversation_id = @conversationId and t.deleted_at is null and t.kind = 'commitment' order by t.id",
            new { conversationId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Applies a user's edit inside the caller's transaction and returns the task, or null when it
    /// does not exist or is deleted. <paramref name="text"/> and <paramref name="done"/> are null
    /// when the request left them out, and so are <paramref name="person"/> and <paramref name="due"/>. An edit of the text marks the
    /// task <c>edited</c>, and so does reopening it or changing its person or due date: a task the user touched is
    /// never removed by a later summary. A person that does not exist throws a foreign key violation.
    /// </summary>
    public async Task<TaskUpdate?> UpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string? text, bool? done,
        PersonChange? person, DueChange? due, DateTimeOffset now, CancellationToken ct)
    {
        var current = await connection.QuerySingleOrDefaultAsync<Current>(new CommandDefinition(
            "select text as Text, done as Done, person_id as PersonId, due_at as Due from tasks where id = @id and deleted_at is null for update",
            new { id }, transaction, cancellationToken: ct));
        if (current is not { } before)
        {
            return null;
        }

        var textChanged = text is not null && text != before.Text;
        var completed = done == true && !before.Done;
        var reopened = done == false && before.Done;
        var personChanged = person is not null && person.PersonId != before.PersonId;
        var dueChanged = due is not null && due.Due != before.Due;
        if (textChanged || completed || reopened || personChanged || dueChanged)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                update tasks
                set text = @text,
                    done = @done,
                    done_at = case when @completed then @now when @reopened then null else done_at end,
                    person_id = case when @personChanged then @personId else person_id end,
                    due_at = case when @dueChanged then @dueAt else due_at end,
                    edited = edited or @textChanged or @reopened or @personChanged or @dueChanged,
                    updated_at = @now
                where id = @id
                """,
                new
                {
                    id, text = textChanged ? text : before.Text, done = done ?? before.Done,
                    completed, reopened, textChanged, personChanged, personId = person?.PersonId, dueChanged, dueAt = due?.Due, now,
                },
                transaction, cancellationToken: ct));
        }

        var task = await connection.QuerySingleAsync<TaskRow>(new CommandDefinition(
            Select + "\nwhere t.id = @id", new { id }, transaction, cancellationToken: ct));
        return new TaskUpdate(task, completed);
    }

    /// <summary>
    /// Adds a task by hand, inside the caller's transaction, marked <c>edited</c> so a later summary never
    /// removes or rewrites it. <paramref name="conversationId"/> may be null: a task needs no conversation.
    /// <see cref="TaskCreateProblem.NoConversation"/> or <see cref="TaskCreateProblem.NoPerson"/>
    /// when either id does not exist; <see cref="TaskCreateProblem.Duplicate"/> when the conversation already
    /// has a task with this fingerprint or, without a conversation, an open task made by hand has it, so a retried
    /// request adds nothing.
    /// </summary>
    public async Task<TaskCreated> CreateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? conversationId, string text, string fingerprint,
        Guid? personId, string kind, DateTime? due, DateTimeOffset now, CancellationToken ct)
    {
        if (conversationId is not null && await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "select id from conversations where id = @conversationId for share",
                new { conversationId }, transaction, cancellationToken: ct)) is null)
        {
            return new TaskCreated(null, TaskCreateProblem.NoConversation);
        }

        if (personId is not null && await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "select id from people where id = @personId", new { personId }, transaction, cancellationToken: ct)) is null)
        {
            return new TaskCreated(null, TaskCreateProblem.NoPerson);
        }

        var id = Guid.CreateVersion7(now);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into tasks (id, conversation_id, text, fingerprint, person_id, kind, due_at, edited, created_at, updated_at)
            values (@id, @conversationId, @text, @fingerprint, @personId, @kind, @due, true, @now, @now)
            on conflict do nothing
            """,
            new { id, conversationId, text, fingerprint, personId, kind, due, now }, transaction, cancellationToken: ct));
        if (inserted == 0)
        {
            return new TaskCreated(null, TaskCreateProblem.Duplicate);
        }

        var task = await connection.QuerySingleAsync<TaskRow>(new CommandDefinition(
            Select + "\nwhere t.id = @id", new { id }, transaction, cancellationToken: ct));
        return new TaskCreated(task, TaskCreateProblem.None);
    }

    private sealed record Pair(string Status, Guid TaskId, Guid DuplicateOf, Guid? ConversationId);

    /// <summary>
    /// Answers a possible duplicate (<c>task_duplicate_suggestions</c>). Accept deletes the newer task, keeping it as a
    /// tombstone so its summary does not bring it back, and links its conversation to the earlier one in
    /// <c>task_duplicates</c>; reject keeps both. <see cref="SuggestionDecision.NotPending"/> when it was answered or either
    /// task is done or deleted since. On accept the result carries the kept task's id.
    /// </summary>
    public async Task<(SuggestionDecision Decision, Guid? Kept)> DecideDuplicateAsync(Guid id, bool accept, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var pair = await connection.QuerySingleOrDefaultAsync<Pair>(new CommandDefinition(
            """
            select s.status as Status, s.task_id as TaskId, s.duplicate_of as DuplicateOf, n.conversation_id as ConversationId
            from task_duplicate_suggestions s join tasks n on n.id = s.task_id
            where s.id = @id
            for update of s
            """,
            new { id }, transaction, cancellationToken: ct));
        if (pair is null)
        {
            return (SuggestionDecision.NotFound, null);
        }

        var live = await connection.QueryAsync<Guid>(new CommandDefinition(
            "select id from tasks where id in (@TaskId, @DuplicateOf) and done = false and deleted_at is null for update",
            new { pair.TaskId, pair.DuplicateOf }, transaction, cancellationToken: ct));
        if (pair.Status != "pending" || live.Count() != 2)
        {
            return (SuggestionDecision.NotPending, null);
        }

        if (accept)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                update tasks set deleted_at = @now, updated_at = @now where id = @TaskId;
                insert into task_duplicates (conversation_id, task_id, created_at) values (@ConversationId, @DuplicateOf, @now)
                on conflict (conversation_id, task_id) do nothing;
                """,
                new { pair.TaskId, pair.DuplicateOf, pair.ConversationId, now }, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update task_duplicate_suggestions set status = @status, decided_at = @now where id = @id",
            new { id, status = accept ? "accepted" : "rejected", now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return (SuggestionDecision.Ok, accept ? pair.DuplicateOf : null);
    }

    /// <summary>A task as <see cref="ListAsync"/> shows it, deleted or not; null when there is no such row.</summary>
    public async Task<TaskRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<TaskRow>(new CommandDefinition(
            Select + "\nwhere t.id = @id", new { id }, cancellationToken: ct));
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
    /// transaction. A returned fingerprint that has a row, in any state, adds no row; a new one
    /// inserts a task; an untouched row (open, not edited, not deleted) the model no longer returns
    /// is deleted. An untouched row the model now gives another kind takes that kind. Returns the ids of the
    /// commitments that are new: inserted, or promoted from an idea. An idea raises no <c>task.created</c>.
    /// <para>
    /// A task the model words differently (a new fingerprint) is inserted as a new task and publishes
    /// <c>task.created</c> again, while its old row is deleted or, if the user touched it, stays. The
    /// spec allows this; v0.4's webhooks will see the second event.
    /// </para>
    /// <para>
    /// NYTKA-75: a fingerprint this conversation has not seen before is compared with the open (not done, not
    /// deleted) tasks of other conversations and those made by hand (<see cref="TaskMatch"/>). A sure match inserts
    /// no row; it is recorded in <c>task_duplicates</c> instead, since a task's <c>conversation_id</c> cannot name a
    /// second conversation. An unsure one inserts the task and a pending <c>task_duplicate_suggestions</c> row for the
    /// review inbox.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ReconcileAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, IReadOnlyList<AiTask> returned,
        DateTimeOffset now, CancellationToken ct)
    {
        var existing = (await connection.QueryAsync<Existing>(
            new CommandDefinition(
                """
                select id as Id, fingerprint as Fingerprint, done as Done, edited as Edited, deleted_at as DeletedAt, kind as Kind, due_at as Due
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

        var created = new List<Guid>();
        var inserted = 0;
        List<OpenTask>? open = null;
        foreach (var task in returned)
        {
            if (!known.Add(task.Fingerprint))
            {
                var row = existing.FirstOrDefault(t => t.Fingerprint == task.Fingerprint);
                if (row is { Done: false, Edited: false, DeletedAt: null } && (row.Kind != task.Kind || row.Due != task.Due))
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "update tasks set kind = @Kind, due_at = @Due, updated_at = @now where id = @Id",
                        new { row.Id, task.Kind, task.Due, now }, transaction, cancellationToken: ct));
                    if (row.Kind != task.Kind && task.Kind == TaskKinds.Commitment)
                    {
                        created.Add(row.Id);
                    }
                }

                continue;
            }

            open ??= (await connection.QueryAsync<OpenTask>(new CommandDefinition(
                """
                select id as Id, fingerprint as Fingerprint, person_id as PersonId, kind as Kind from tasks
                where conversation_id is distinct from @conversationId and done = false and deleted_at is null
                order by id
                """,
                new { conversationId }, transaction, cancellationToken: ct))).ToList();
            var match = TaskMatch.Find(task, open);
            if (match.Sure is { } existingTaskId)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into task_duplicates (conversation_id, task_id, created_at) values (@conversationId, @existingTaskId, @now)
                    on conflict (conversation_id, task_id) do nothing
                    """,
                    new { conversationId, existingTaskId, now }, transaction, cancellationToken: ct));
                continue;
            }

            // A millisecond apart, so ids keep the order the model gave the tasks in.
            var id = Guid.CreateVersion7(now.AddMilliseconds(inserted++));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into tasks (id, conversation_id, text, fingerprint, person_id, kind, due_at, created_at, updated_at)
                values (@id, @conversationId, @Text, @Fingerprint, (select id from people where id = @PersonId), @Kind, @Due, @now, @now)
                """,
                new { id, conversationId, task.Text, task.Fingerprint, task.PersonId, task.Kind, task.Due, now }, transaction, cancellationToken: ct));
            if (match.Unsure is { } duplicateOf)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into task_duplicate_suggestions (id, task_id, duplicate_of, created_at)
                    values (@suggestion, @id, @duplicateOf, @now)
                    """,
                    new { suggestion = Guid.CreateVersion7(now), id, duplicateOf, now }, transaction, cancellationToken: ct));
            }

            if (task.Kind == TaskKinds.Commitment)
            {
                created.Add(id);
            }
        }

        return created;
    }
}
