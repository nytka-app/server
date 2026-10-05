using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A tag in use and how many conversations and people hold it.</summary>
public sealed record TagCount(string Name, int Conversations, int People)
{
    public int Uses => Conversations + People;
}

public enum TagAdd { Added, Present, NoItem, TooMany }

public enum TagWrite { Ok, NotFound, NameTaken }

/// <summary>
/// Tags on conversations and people (<c>tags</c>, <c>conversation_tags</c>, <c>person_tags</c>). A tag with no link
/// left is deleted by a trigger, so nothing here removes one by hand except <see cref="DeleteAsync"/>. The static
/// methods run in the caller's transaction, so a task that proposes or accepts tags adds them with its own writes.
/// </summary>
public sealed class TagStore(NpgsqlDataSource dataSource)
{
    public const int MaxPerItem = 20;

    /// <summary>The link table, its item column and the item's table.</summary>
    private sealed record Link(string Table, string Column, string Items);

    private static readonly Link Conversations = new("conversation_tags", "conversation_id", "conversations");

    private static readonly Link People = new("person_tags", "person_id", "people");

    private sealed record Tagged(Guid ItemId, string Name);

    /// <summary>Tags in use, most used first, then by name; <paramref name="prefix"/> (already normalized) keeps names starting with it.</summary>
    public async Task<IReadOnlyList<TagCount>> ListAsync(string? prefix, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<TagCount>(new CommandDefinition(
            $"""
            select * from ({CountSelect}) t
            where cast(@prefix as text) is null or starts_with(t.name, @prefix)
            order by t.conversations + t.people desc, t.name
            """,
            new { prefix }, cancellationToken: ct))).ToList();
    }

    /// <summary>The names of the <paramref name="limit"/> tags most in use, then by name: what the model is shown to reuse.</summary>
    public async Task<IReadOnlyList<string>> NamesInUseAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<string>(new CommandDefinition(
            $"select name from ({CountSelect}) t order by t.conversations + t.people desc, t.name limit @limit",
            new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<TagCount?> GetAsync(string name, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await GetAsync(connection, null, name, ct);
    }

    private const string CountSelect =
        """
        select t.name as Name,
               (select count(*)::int from conversation_tags c where c.tag_id = t.id) as Conversations,
               (select count(*)::int from person_tags p where p.tag_id = t.id) as People
        from tags t
        """;

    private static Task<TagCount?> GetAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string name, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<TagCount>(new CommandDefinition(
            $"{CountSelect} where t.name = @name", new { name }, transaction, cancellationToken: ct));

    /// <summary>The tags of one conversation, sorted by name.</summary>
    public async Task<IReadOnlyList<string>> OfConversationAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await OfAsync(connection, null, Conversations, [id], ct)).GetValueOrDefault(id) ?? [];
    }

    /// <summary>The tags of one person, sorted by name.</summary>
    public async Task<IReadOnlyList<string>> OfPersonAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await OfAsync(connection, null, People, [id], ct)).GetValueOrDefault(id) ?? [];
    }

    /// <summary>The tags of each conversation of <paramref name="ids"/> in one query; one with none is absent.</summary>
    public static Task<Dictionary<Guid, string[]>> OfConversationsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        OfAsync(connection, transaction, Conversations, ids, ct);

    /// <summary>The tags of each person of <paramref name="ids"/> in one query; one with none is absent.</summary>
    public static Task<Dictionary<Guid, string[]>> OfPeopleAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        OfAsync(connection, transaction, People, ids, ct);

    private static async Task<Dictionary<Guid, string[]>> OfAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Link link, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<Tagged>(new CommandDefinition(
            $"""
            select l.{link.Column} as ItemId, t.name as Name
            from {link.Table} l join tags t on t.id = l.tag_id
            where l.{link.Column} = any (@ids)
            order by t.name
            """,
            new { ids = ids.ToArray() }, transaction, cancellationToken: ct));
        return rows.GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g => g.Select(r => r.Name).ToArray());
    }

    public Task<TagAdd> AddToConversationAsync(Guid id, string name, DateTimeOffset now, CancellationToken ct) =>
        AddInTransactionAsync(Conversations, id, name, now, ct);

    public Task<TagAdd> AddToPersonAsync(Guid id, string name, DateTimeOffset now, CancellationToken ct) =>
        AddInTransactionAsync(People, id, name, now, ct);

    private async Task<TagAdd> AddInTransactionAsync(Link link, Guid id, string name, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var result = await AddAsync(connection, transaction, link, id, name, now, ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>Adds <paramref name="name"/> (already normalized) to a conversation in the caller's transaction.</summary>
    public static Task<TagAdd> AddToConversationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string name, DateTimeOffset now, CancellationToken ct) =>
        AddAsync(connection, transaction, Conversations, id, name, now, ct);

    /// <summary>Adds <paramref name="name"/> (already normalized) to a person in the caller's transaction.</summary>
    public static Task<TagAdd> AddToPersonAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string name, DateTimeOffset now, CancellationToken ct) =>
        AddAsync(connection, transaction, People, id, name, now, ct);

    /// <summary>
    /// Takes the item's row so two adds cannot both pass the limit, then finds or creates the tag and links it. The
    /// upsert holds the tag's row until commit, so the trigger that drops unused tags skips it.
    /// </summary>
    private static async Task<TagAdd> AddAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Link link, Guid id, string name, DateTimeOffset now, CancellationToken ct)
    {
        if (await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            $"select 1 from {link.Items} where id = @id for update", new { id }, transaction, cancellationToken: ct)) is null)
        {
            return TagAdd.NoItem;
        }

        var held = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"select exists (select 1 from {link.Table} l join tags t on t.id = l.tag_id where l.{link.Column} = @id and t.name = @name)",
            new { id, name }, transaction, cancellationToken: ct));
        if (held)
        {
            return TagAdd.Present;
        }

        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*)::int from {link.Table} where {link.Column} = @id", new { id }, transaction, cancellationToken: ct));
        if (count >= MaxPerItem)
        {
            return TagAdd.TooMany;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            with tag as (
                insert into tags (id, name, created_at) values (@tagId, @name, @now)
                on conflict (name) do update set name = excluded.name
                returning id)
            insert into {link.Table} ({link.Column}, tag_id, created_at)
            select @id, id, @now from tag
            on conflict do nothing
            """,
            new { tagId = Guid.NewGuid(), id, name, now }, transaction, cancellationToken: ct));
        return TagAdd.Added;
    }

    /// <summary>Removes the tag from the conversation; true when the conversation exists, also when it did not hold the tag.</summary>
    public Task<bool> RemoveFromConversationAsync(Guid id, string name, CancellationToken ct) => RemoveAsync(Conversations, id, name, ct);

    /// <summary>Removes the tag from the person; true when the person exists, also when they did not hold the tag.</summary>
    public Task<bool> RemoveFromPersonAsync(Guid id, string name, CancellationToken ct) => RemoveAsync(People, id, name, ct);

    private async Task<bool> RemoveAsync(Link link, Guid id, string name, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"select exists (select 1 from {link.Items} where id = @id)", new { id }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            $"delete from {link.Table} where {link.Column} = @id and tag_id = (select id from tags where name = @name)",
            new { id, name }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return exists;
    }

    /// <summary>Renames a tag. <see cref="TagWrite.NameTaken"/> when <paramref name="newName"/> (normalized) is in use by another tag.</summary>
    public async Task<TagWrite> RenameAsync(string name, string newName, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            return await connection.ExecuteAsync(new CommandDefinition(
                "update tags set name = @newName where name = @name", new { name, newName }, cancellationToken: ct)) == 1
                ? TagWrite.Ok
                : TagWrite.NotFound;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return TagWrite.NameTaken;
        }
    }

    /// <summary>
    /// Moves every link of <paramref name="name"/> to <paramref name="into"/> (created when it does not exist), drops
    /// duplicates and deletes <paramref name="name"/>. Both names are normalized. The result is the target's counts, or null
    /// when <paramref name="name"/> is no tag. The links ignore the 20-per-item limit, as a merge of two items does.
    /// </summary>
    public async Task<TagCount?> MergeAsync(string name, string into, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var source = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select id from tags where name = @name for update", new { name }, transaction, cancellationToken: ct));
        if (source is null)
        {
            return null;
        }

        if (name != into)
        {
            var target = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
                """
                insert into tags (id, name, created_at) values (@tagId, @into, @now)
                on conflict (name) do update set name = excluded.name
                returning id
                """,
                new { tagId = Guid.NewGuid(), into, now }, transaction, cancellationToken: ct));
            foreach (var link in new[] { Conversations, People })
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    $"""
                    insert into {link.Table} ({link.Column}, tag_id, created_at)
                    select {link.Column}, @target, created_at from {link.Table} where tag_id = @source
                    on conflict do nothing
                    """,
                    new { source, target }, transaction, cancellationToken: ct));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "delete from tags where id = @source", new { source }, transaction, cancellationToken: ct));
        }

        var counts = await GetAsync(connection, transaction, into, ct);
        await transaction.CommitAsync(ct);
        return counts;
    }

    /// <summary>Deletes the tag and its links. False when there is no such tag.</summary>
    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from tags where name = @name", new { name }, cancellationToken: ct)) == 1;
    }

    /// <summary>
    /// Gives the tags of <paramref name="from"/> conversations to <paramref name="to"/> in the caller's transaction, before
    /// the others are deleted. A tag the survivor holds already is dropped, not an error.
    /// </summary>
    public static Task MoveConversationTagsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] from, Guid to, CancellationToken ct) =>
        MoveAsync(connection, transaction, Conversations, from, to, ct);

    /// <summary>As <see cref="MoveConversationTagsAsync"/>, for the tags of a person merged into another.</summary>
    public static Task MovePersonTagsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid from, Guid to, CancellationToken ct) =>
        MoveAsync(connection, transaction, People, [from], to, ct);

    private static Task MoveAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Link link, Guid[] from, Guid to, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            $"""
            insert into {link.Table} ({link.Column}, tag_id, created_at)
            select @to, tag_id, min(created_at) from {link.Table} where {link.Column} = any (@from) group by tag_id
            on conflict do nothing
            """,
            new { from, to }, transaction, cancellationToken: ct));
}
