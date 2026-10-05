using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// A live fact with the conversation it was taken from. <c>Basis</c> is null for a fact added by hand, and the
/// <c>Conversation*</c> fields are null for it too.
/// </summary>
public sealed record PersonFactRow(
    Guid Id, Guid PersonId, string Text, string Source, string? Basis, Guid? ConversationId, string? ConversationTitle,
    long? SegmentId, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>A page of facts and the id to pass as <c>before</c> for the next one, or null on the last page.</summary>
public sealed record PersonFactPage(IReadOnlyList<PersonFactRow> Items, Guid? NextBefore);

/// <summary>A person as the prompt names them.</summary>
public sealed record PersonRef(Guid Id, string Name);

/// <summary>A fact as the prompt lists it under its person.</summary>
public sealed record KnownFact(Guid PersonId, string Text);

/// <summary>
/// A segment as fact extraction reads it. <see cref="PersonId"/> is the person the label rule gives it (null when
/// none) and <see cref="IsUser"/> whether it is the wearer's, which wins over any person.
/// </summary>
public sealed record FactSegment(long Id, DateTime StartedAt, string? Speaker, string Text, bool IsUser, Guid? PersonId);

/// <summary>A conversation as fact extraction reads it. <see cref="LastSegmentId"/> is the highest segment id among <see cref="Segments"/>.</summary>
public sealed record FactInput(string? Title, DateTime StartedAt, long? LastSegmentId, IReadOnlyList<FactSegment> Segments);

/// <summary>A fact the model proposed, after the server set its basis.</summary>
public sealed record FactCandidate(Guid PersonId, string Text, string Fingerprint, string Basis, long SegmentId);

public sealed record PersonFactRun(string Status, long? ThroughSegmentId, int Failures);

/// <summary>Facts about people (<c>person_facts</c>, and <c>people_runs</c> of kind <c>facts</c>).</summary>
public sealed class PersonFactStore(NpgsqlDataSource dataSource)
{
    public const string Facts = "facts";
    public const int KnownPerPerson = 30;

    /// <summary>
    /// Newest first by id (ids are UUID v7). Deleted facts are left out. <c>NextBefore</c> is the last id of a page
    /// that has more behind it, else null. Null when there is no such person.
    /// </summary>
    public async Task<PersonFactPage?> ListAsync(Guid personId, Guid? before, int limit, CancellationToken ct)
    {
        if (!await ExistsAsync(personId, ct))
        {
            return null;
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<PersonFactRow>(new CommandDefinition(
            $"""
            {SelectFact}
            where f.person_id = @personId and f.deleted_at is null
              and (cast(@before as uuid) is null or f.id < cast(@before as uuid))
            order by f.id desc
            limit @fetch
            """,
            new { personId, before, fetch = limit + 1 }, cancellationToken: ct))).ToList();
        var items = rows.Take(limit).ToList();
        return new PersonFactPage(items, rows.Count > limit ? items[^1].Id : null);
    }

    public async Task<bool> ExistsAsync(Guid personId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from people where id = @personId)", new { personId }, cancellationToken: ct));
    }

    public async Task<PersonFactRow?> GetAsync(Guid personId, Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PersonFactRow>(new CommandDefinition(
            $"{SelectFact} where f.id = @id and f.person_id = @personId and f.deleted_at is null",
            new { id, personId }, cancellationToken: ct));
    }

    /// <summary>
    /// Adds a fact by hand, inside the caller's transaction. The same fact deleted earlier is revived as a new row
    /// (a tombstone is dropped, so the fact is the newest). Null when a live fact already holds it or the person is gone.
    /// </summary>
    public async Task<Guid?> AddAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid personId, string text, string fingerprint,
        DateTimeOffset now, CancellationToken ct)
    {
        if (!await LockPersonAsync(connection, transaction, personId, ct))
        {
            return null;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from person_facts where person_id = @personId and fingerprint = @fingerprint and deleted_at is not null",
            new { personId, fingerprint }, transaction, cancellationToken: ct));
        var id = Guid.CreateVersion7(now);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into person_facts (id, person_id, text, fingerprint, source, created_at, updated_at)
            values (@id, @personId, @text, @fingerprint, 'user', @now, @now)
            on conflict (person_id, fingerprint) do nothing
            """,
            new { id, personId, text, fingerprint, now }, transaction, cancellationToken: ct));
        return inserted > 0 ? id : null;
    }

    /// <summary>Edits a live fact: the text changes, the fingerprint stays, and extraction never rewrites it.</summary>
    public async Task<bool> UpdateTextAsync(Guid personId, Guid id, string text, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "update person_facts set text = @text, edited = true, updated_at = @now where id = @id and person_id = @personId and deleted_at is null",
            new { id, personId, text, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>Leaves a tombstone, so extraction does not bring the fact back. False when there is no live fact.</summary>
    public async Task<bool> DeleteAsync(Guid personId, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "update person_facts set deleted_at = @now, updated_at = @now where id = @id and person_id = @personId and deleted_at is null",
            new { id, personId, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>Every person, for matching names in the transcript.</summary>
    public async Task<IReadOnlyList<PersonRef>> PeopleAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await PeopleAsync(connection, null, ct);
    }

    public async Task<IReadOnlyList<PersonRef>> PeopleAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct) =>
        (await connection.QueryAsync<PersonRef>(new CommandDefinition(
            "select id as Id, name as Name from people order by lower(name), id", transaction: transaction, cancellationToken: ct))).ToList();

    /// <summary>The newest live facts of each person, <see cref="KnownPerPerson"/> at most, for the prompt.</summary>
    public async Task<IReadOnlyList<KnownFact>> KnownAsync(IReadOnlyCollection<Guid> personIds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnownFact>(new CommandDefinition(
            """
            select person_id as PersonId, text as Text
            from (select person_id, text, id, row_number() over (partition by person_id order by id desc) as n
                  from person_facts where person_id = any(@personIds) and deleted_at is null) newest
            where n <= @limit
            order by person_id, id desc
            """,
            new { personIds = personIds.ToArray(), limit = KnownPerPerson }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// The conversation's title, day and transcript, or null when it is gone. A segment carries the person the label
    /// rule gives it and whether it is the wearer's.
    /// </summary>
    public async Task<FactInput?> ReadInputAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid conversationId, CancellationToken ct)
    {
        var header = await connection.QuerySingleOrDefaultAsync<Header>(new CommandDefinition(
            "select coalesce(title, ai_title) as Title, started_at as StartedAt from conversations where id = @conversationId",
            new { conversationId }, transaction, cancellationToken: ct));
        if (header is null)
        {
            return null;
        }

        var rows = (await connection.QueryAsync<FactSegment>(new CommandDefinition(
            $"""
            select s.id as Id, s.started_at as StartedAt, {SpeakerLabel.Column} as Speaker, s.text as Text,
                   ({SpeakerLabel.IsUser}) is true as IsUser, {SpeakerLabel.PersonId} as PersonId
            from segments s {SpeakerLabel.Joins}
            where s.conversation_id = @conversationId
            order by s.started_at, s.id
            """,
            new { conversationId }, transaction, cancellationToken: ct))).ToList();
        return new FactInput(header.Title, header.StartedAt, rows.Count == 0 ? null : rows.Max(r => r.Id), rows);
    }

    public async Task<FactInput?> ReadInputAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ReadInputAsync(connection, null, conversationId, ct);
    }

    public async Task<PersonFactRun?> GetRunAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PersonFactRun>(new CommandDefinition(
            """
            select status as Status, through_segment_id as ThroughSegmentId, failures as Failures
            from people_runs where conversation_id = @conversationId and kind = 'facts'
            """,
            new { conversationId }, cancellationToken: ct));
    }

    /// <summary>
    /// Records that an extraction is queued, inside the transaction of the summary that asked for it, and returns
    /// whether the caller should queue the job. False when the last run already read every segment. The failure
    /// count starts again only when segments arrived after that run.
    /// </summary>
    public async Task<bool> MarkPendingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, DateTimeOffset now, CancellationToken ct)
    {
        var last = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select max(id) from segments where conversation_id = @conversationId",
            new { conversationId }, transaction, cancellationToken: ct));
        var through = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select through_segment_id from people_runs where conversation_id = @conversationId and kind = 'facts' for update",
            new { conversationId }, transaction, cancellationToken: ct));
        if (last is null || through >= last)
        {
            return false;
        }

        var fresh = through is not null;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into people_runs (conversation_id, kind, status, updated_at) values (@conversationId, 'facts', 'pending', @now)
            on conflict (conversation_id, kind) do update
            set status = 'pending', message = null, updated_at = @now,
                failures = case when @fresh then 0 else people_runs.failures end
            """,
            new { conversationId, now, fresh }, transaction, cancellationToken: ct));
        return true;
    }

    /// <summary>Drops the record of a run that will not happen (extraction is off or has no model), so it never stays pending.</summary>
    public async Task ForgetRunAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from people_runs where conversation_id = @conversationId and kind = 'facts' and status = 'pending'",
            new { conversationId }, cancellationToken: ct));
    }

    /// <summary>
    /// Applies the candidates in the caller's transaction and returns the ids of the facts it inserted. A candidate
    /// whose person already holds its fingerprint, a deleted fact included, is dropped, and so is one whose person is
    /// gone. Nothing is rewritten.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ApplyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId,
        IReadOnlyList<FactCandidate> candidates, DateTimeOffset now, CancellationToken ct)
    {
        var inserted = new List<Guid>();
        foreach (var candidate in candidates)
        {
            if (candidate.Fingerprint.Length == 0 || !await LockPersonAsync(connection, transaction, candidate.PersonId, ct))
            {
                continue;
            }

            var id = Guid.CreateVersion7(now);
            if (await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into person_facts (id, person_id, text, fingerprint, source, basis, conversation_id, segment_id, created_at, updated_at)
                values (@id, @personId, @text, @fingerprint, 'ai', @basis, @conversationId, @segmentId, @now, @now)
                on conflict (person_id, fingerprint) do nothing
                """,
                new
                {
                    id, personId = candidate.PersonId, text = candidate.Text, fingerprint = candidate.Fingerprint,
                    basis = candidate.Basis, conversationId, segmentId = candidate.SegmentId, now,
                },
                transaction, cancellationToken: ct)) > 0)
            {
                inserted.Add(id);
            }
        }

        return inserted;
    }

    /// <summary>Records a finished run and the highest segment id it read, in the caller's transaction.</summary>
    public Task MarkDoneAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, long? throughSegmentId,
        DateTimeOffset now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            insert into people_runs (conversation_id, kind, status, through_segment_id, updated_at)
            values (@conversationId, 'facts', 'done', @throughSegmentId, @now)
            on conflict (conversation_id, kind) do update
            set status = 'done', through_segment_id = @throughSegmentId, failures = 0, message = null, updated_at = @now
            """,
            new { conversationId, throughSegmentId, now }, transaction, cancellationToken: ct));

    /// <summary>Records a run that failed its attempts and returns the failure count; null when the conversation is gone.</summary>
    public async Task<int?> MarkFailedAsync(Guid conversationId, string message, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            insert into people_runs (conversation_id, kind, status, failures, message, updated_at)
            select id, 'facts', 'failed', 1, @message, @now from conversations where id = @conversationId
            on conflict (conversation_id, kind) do update
            set status = 'failed', failures = people_runs.failures + 1, message = @message, updated_at = @now
            returning failures
            """,
            new { conversationId, message, now }, cancellationToken: ct));
    }

    /// <summary>Whether the person exists, locking the row until the transaction ends so it cannot be deleted before the insert.</summary>
    public static async Task<bool> LockPersonAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid personId, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select id from people where id = @personId for share", new { personId }, transaction, cancellationToken: ct)) is not null;

    private const string SelectFact =
        """
        select f.id as Id, f.person_id as PersonId, f.text as Text, f.source as Source, f.basis as Basis,
               f.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as ConversationTitle,
               f.segment_id as SegmentId, f.created_at as CreatedAt, f.updated_at as UpdatedAt
        from person_facts f
        left join conversations c on c.id = f.conversation_id
        """;

    private sealed record Header(string? Title, DateTime StartedAt);
}
