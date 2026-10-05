using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// A tag the model proposed. <paramref name="PersonId"/> is null for a conversation's tag; <paramref name="ConversationId"/> is
/// where the proposal came from either way.
/// </summary>
public sealed record TagSuggestionRow(Guid Id, Guid ConversationId, Guid? PersonId, string? PersonName, string Name, DateTime CreatedAt);

public enum TagDecision { Ok, NotFound, NotPending, TooMany }

/// <summary>
/// Proposed tags (<c>tag_suggestions</c>, docs/specs/tags.md). A row holds a name, not a tag: nothing here links a tag. Only
/// <see cref="AcceptAsync"/> does, through <see cref="TagStore"/>, so a proposal changes nothing until the owner accepts it.
/// No answer, error or log line carries a tag name.
/// </summary>
public sealed class TagSuggestionStore(NpgsqlDataSource dataSource)
{
    /// <summary>Proposals with the given status, newest first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<TagSuggestionRow>> ListAsync(string status, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<TagSuggestionRow>(new CommandDefinition(
            """
            select s.id as Id, s.conversation_id as ConversationId, s.person_id as PersonId, p.name as PersonName,
                   s.name as Name, s.created_at as CreatedAt
            from tag_suggestions s
            left join people p on p.id = s.person_id
            where s.status = @status
            order by s.created_at desc, s.id desc
            limit @limit
            """,
            new { status, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Stores <paramref name="names"/> (already normalized) as pending proposals for a conversation, or for a person when
    /// <paramref name="personId"/> is set, in the caller's transaction. A name already stored for the item, in any status, is
    /// skipped. Returns the number inserted.
    /// </summary>
    public static async Task<int> AddAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, Guid? personId, IEnumerable<string> names,
        DateTimeOffset now, CancellationToken ct)
    {
        var inserted = 0;
        foreach (var name in names)
        {
            inserted += await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into tag_suggestions (id, conversation_id, person_id, name, created_at)
                values (@id, @conversationId, @personId, @name, @now)
                on conflict do nothing
                """,
                new { id = Guid.CreateVersion7(now), conversationId, personId, name, now }, transaction, cancellationToken: ct));
        }

        return inserted;
    }

    private sealed record Pending(Guid ConversationId, Guid? PersonId, string Name, string Status);

    /// <summary>
    /// Adds the proposed tag to its item in one transaction and marks the proposal accepted; the item's tags follow. A tag the
    /// item holds already counts as added. <see cref="TagDecision.TooMany"/> when the item has 20 tags: the proposal stays pending.
    /// </summary>
    public async Task<(TagDecision Result, IReadOnlyList<string> Tags)> AcceptAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<Pending>(new CommandDefinition(
            """
            select conversation_id as ConversationId, person_id as PersonId, name as Name, status as Status
            from tag_suggestions where id = @id for update
            """,
            new { id }, transaction, cancellationToken: ct));
        if (row is null)
        {
            return (TagDecision.NotFound, []);
        }

        if (row.Status != "pending")
        {
            return (TagDecision.NotPending, []);
        }

        var item = row.PersonId ?? row.ConversationId;
        var added = row.PersonId is null
            ? await TagStore.AddToConversationAsync(connection, transaction, item, row.Name, now, ct)
            : await TagStore.AddToPersonAsync(connection, transaction, item, row.Name, now, ct);
        if (added == TagAdd.TooMany)
        {
            return (TagDecision.TooMany, []);
        }

        if (added == TagAdd.NoItem)
        {
            return (TagDecision.NotFound, []);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update tag_suggestions set status = 'accepted', decided_at = @now where id = @id",
            new { id, now }, transaction, cancellationToken: ct));
        var tags = row.PersonId is null
            ? await TagStore.OfConversationsAsync(connection, transaction, [item], ct)
            : await TagStore.OfPeopleAsync(connection, transaction, [item], ct);
        await transaction.CommitAsync(ct);
        return (TagDecision.Ok, tags.GetValueOrDefault(item) ?? []);
    }

    /// <summary>Rejects a pending proposal; its name is never proposed again for that item.</summary>
    public async Task<TagDecision> RejectAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        if (await connection.ExecuteAsync(new CommandDefinition(
                "update tag_suggestions set status = 'rejected', decided_at = @now where id = @id and status = 'pending'",
                new { id, now }, cancellationToken: ct)) == 1)
        {
            return TagDecision.Ok;
        }

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from tag_suggestions where id = @id)", new { id }, cancellationToken: ct))
            ? TagDecision.NotPending
            : TagDecision.NotFound;
    }

    /// <summary>
    /// Gives the proposals of <paramref name="from"/> conversations to <paramref name="to"/> in the caller's transaction, before
    /// the others are deleted (they would cascade). Every status moves, so a rejected or accepted name is not proposed again for
    /// the merged conversation. A name the survivor has already keeps its row; a person's proposals only change their source.
    /// </summary>
    public static async Task MoveConversationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] from, Guid to, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into tag_suggestions (id, conversation_id, person_id, name, status, created_at, decided_at)
            select distinct on (name) gen_random_uuid(), @to, null, name, status, created_at, decided_at
            from tag_suggestions where conversation_id = any (@from) and person_id is null
            order by name, case status when 'rejected' then 0 when 'accepted' then 1 else 2 end
            on conflict do nothing
            """,
            new { from, to }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "update tag_suggestions set conversation_id = @to where conversation_id = any (@from) and person_id is not null",
            new { from, to }, transaction, cancellationToken: ct));
    }

    /// <summary>As <see cref="MoveConversationAsync"/>, for the proposals of a person merged into another.</summary>
    public static Task MovePersonAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid from, Guid to, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            insert into tag_suggestions (id, conversation_id, person_id, name, status, created_at, decided_at)
            select distinct on (name) gen_random_uuid(), conversation_id, @to, name, status, created_at, decided_at
            from tag_suggestions where person_id = @from
            order by name, case status when 'rejected' then 0 when 'accepted' then 1 else 2 end
            on conflict do nothing
            """,
            new { from, to }, transaction, cancellationToken: ct));
}
