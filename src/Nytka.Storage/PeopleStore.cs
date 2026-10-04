using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>The label a model or an MCP client reads for the wearer's segments.</summary>
public static class SpeakerLabel
{
    public const string Wearer = "Wearer";

    /// <summary>
    /// SQL for whether a segment aliased <c>s</c> is the wearer's, the one rule every reader follows
    /// (docs/specs/your-voice.md, Which label wins): the wearer's own mark, else Nytka's verdict when Nytka checked the
    /// segment (a checked segment without a verdict is unknown, never the provider's guess), else the provider's.
    /// </summary>
    public const string IsUser = "coalesce(s.is_user_manual, case when s.voice_checked then s.voice_is_user else s.is_user end)";

    /// <summary>
    /// SQL for which step of <see cref="IsUser"/> decided a segment aliased <c>s</c>: <c>manual</c>, <c>voice</c> or
    /// <c>provider</c>, or null when none did (a checked segment without a verdict, or no label at all).
    /// </summary>
    public const string IsUserSource =
        "case when s.is_user_manual is not null then 'manual' "
        + "when s.voice_checked then case when s.voice_is_user is not null then 'voice' end "
        + "when s.is_user is not null then 'provider' end";

    /// <summary>
    /// SQL for the label of a segment aliased <c>s</c>, after <see cref="Joins"/>, following docs/specs/people.md (Which
    /// label wins): the wearer, else the person set on the segment, else the person the voice was named after, else the
    /// provider's own label.
    /// </summary>
    public const string Column = $"case when {IsUser} then '{Wearer}' else coalesce(sp.name, p.name, s.speaker) end";

    /// <summary>SQL for the id of the person a segment aliased <c>s</c> belongs to, after <see cref="Joins"/>; null when none.</summary>
    public const string PersonId = "coalesce(s.person_id, pv.person_id)";

    /// <summary>SQL for the name of that person, after <see cref="Joins"/>.</summary>
    public const string PersonName = "coalesce(sp.name, p.name)";

    public const string Joins =
        "left join person_voices pv on pv.speaker_id = s.speaker_id left join people p on p.id = pv.person_id "
        + "left join people sp on sp.id = s.person_id";
}

public sealed record PersonRow(Guid Id, string Name, string? Note, DateTime CreatedAt, string[] Voices, int Segments);

/// <summary>A conversation the person spoke in.</summary>
public sealed record PersonConversation(Guid Id, string? Title, DateTime StartedAt);

/// <summary>
/// The person page: <see cref="LastSeenAt"/> is the newest segment of the person by the label rule (the wearer's own
/// segments never count). <see cref="VoiceprintSamples"/> is how many segments the voiceprint was made from, 0 with
/// none; the vector itself never leaves the database.
/// </summary>
public sealed record PersonView(
    Guid Id, string Name, string? Note, DateTime CreatedAt, DateTime? LastSeenAt, string[] Voices, bool HasVoiceprint,
    int VoiceprintSamples, IReadOnlyList<PersonConversation> Conversations, IReadOnlyList<PersonFactRow> Facts,
    IReadOnlyList<TaskRow> OpenTasks);

/// <summary>A person for a list: when they were last heard and how many live facts they have.</summary>
public sealed record PersonSummary(Guid Id, string Name, DateTime? LastSeenAt, int Facts);

public enum PersonWrite { Ok, NotFound, NameTaken }

public enum SegmentLink { Ok, NoSegment, NoPerson }

public sealed record UnnamedVoice(string SpeakerId, string? Label, int Segments, DateTime LastSeenAt);

/// <summary>People: names given to the voices a transcription provider tells apart (<c>people</c>, <c>person_voices</c>).</summary>
public sealed class PeopleStore(NpgsqlDataSource dataSource, PersonFactStore facts, TaskStore tasks)
{
    public const int ViewConversations = 10;
    public const int ViewFacts = 50;
    public const int ViewTasks = 100;

    private sealed record Row(Guid Id, string Name, string? Note, DateTime CreatedAt);

    private sealed record Voice(Guid PersonId, string SpeakerId);

    private sealed record Count(Guid PersonId, int Segments);

    private sealed record Header(Guid Id, string Name, string? Note, DateTime CreatedAt, DateTime? LastSeenAt, int? VoiceprintSamples);

    private sealed record Seen(Guid PersonId, DateTime LastSeenAt);

    private sealed record FactCount(Guid PersonId, int Facts);

    public async Task<IReadOnlyList<PersonRow>> ListAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var people = (await connection.QueryAsync<Row>(new CommandDefinition(
            "select id as Id, name as Name, note as Note, created_at as CreatedAt from people order by lower(name)", cancellationToken: ct))).ToList();
        var voices = (await connection.QueryAsync<Voice>(new CommandDefinition(
            "select person_id as PersonId, speaker_id as SpeakerId from person_voices order by speaker_id", cancellationToken: ct))).ToList();
        var counts = (await connection.QueryAsync<Count>(new CommandDefinition(
            $"""
            select {SpeakerLabel.PersonId} as PersonId, count(*)::int as Segments
            from segments s {SpeakerLabel.Joins}
            where {SpeakerLabel.IsUser} is not true and {SpeakerLabel.PersonId} is not null
            group by {SpeakerLabel.PersonId}
            """, cancellationToken: ct))).ToDictionary(c => c.PersonId, c => c.Segments);
        return people
            .Select(p => new PersonRow(
                p.Id, p.Name, p.Note, p.CreatedAt,
                voices.Where(v => v.PersonId == p.Id).Select(v => v.SpeakerId).ToArray(),
                counts.GetValueOrDefault(p.Id)))
            .ToList();
    }

    public async Task<PersonRow?> GetAsync(Guid id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(p => p.Id == id);

    /// <summary>Everyone, most recently heard first (never heard last), then by name.</summary>
    public async Task<IReadOnlyList<PersonSummary>> SummariesAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var people = (await connection.QueryAsync<Row>(new CommandDefinition(
            "select id as Id, name as Name, note as Note, created_at as CreatedAt from people", cancellationToken: ct))).ToList();
        var seen = (await connection.QueryAsync<Seen>(new CommandDefinition(
            $"""
            select {SpeakerLabel.PersonId} as PersonId, max(s.started_at) as LastSeenAt
            from segments s {SpeakerLabel.Joins}
            where {SpeakerLabel.IsUser} is not true and {SpeakerLabel.PersonId} is not null
            group by {SpeakerLabel.PersonId}
            """, cancellationToken: ct))).ToDictionary(r => r.PersonId, r => r.LastSeenAt);
        var counts = (await connection.QueryAsync<FactCount>(new CommandDefinition(
            "select person_id as PersonId, count(*)::int as Facts from person_facts where deleted_at is null group by person_id",
            cancellationToken: ct))).ToDictionary(r => r.PersonId, r => r.Facts);
        return people
            .Select(p => new PersonSummary(p.Id, p.Name, seen.TryGetValue(p.Id, out var at) ? at : null, counts.GetValueOrDefault(p.Id)))
            .OrderByDescending(p => p.LastSeenAt.HasValue).ThenByDescending(p => p.LastSeenAt)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The person page, or null when there is no such person.</summary>
    public async Task<PersonView?> ViewAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var header = await connection.QuerySingleOrDefaultAsync<Header>(new CommandDefinition(
            $"""
            select pe.id as Id, pe.name as Name, pe.note as Note, pe.created_at as CreatedAt,
                   (select max(s.started_at) from segments s {SpeakerLabel.Joins}
                    where {SpeakerLabel.PersonId} = pe.id and {SpeakerLabel.IsUser} is not true) as LastSeenAt,
                   (select vp.count from person_voiceprints vp where vp.person_id = pe.id) as VoiceprintSamples
            from people pe where pe.id = @id
            """, new { id }, cancellationToken: ct));
        if (header is null)
        {
            return null;
        }

        var voices = (await connection.QueryAsync<string>(new CommandDefinition(
            "select speaker_id from person_voices where person_id = @id order by speaker_id", new { id }, cancellationToken: ct))).ToArray();
        var conversations = (await connection.QueryAsync<PersonConversation>(new CommandDefinition(
            $"""
            select c.id as Id, coalesce(c.title, c.ai_title) as Title, c.started_at as StartedAt
            from conversations c
            where exists (select 1 from segments s {SpeakerLabel.Joins}
                          where s.conversation_id = c.id and {SpeakerLabel.PersonId} = @id and {SpeakerLabel.IsUser} is not true)
            order by c.started_at desc, c.id desc
            limit @limit
            """, new { id, limit = ViewConversations }, cancellationToken: ct))).ToList();
        var page = await facts.ListAsync(id, null, ViewFacts, ct);
        return new PersonView(
            header.Id, header.Name, header.Note, header.CreatedAt, header.LastSeenAt, voices, header.VoiceprintSamples is not null,
            header.VoiceprintSamples ?? 0, conversations, page?.Items ?? [], await tasks.OpenForPersonAsync(id, ViewTasks, ct));
    }

    /// <summary>
    /// Gives <paramref name="speakerId"/> the name: the person with that name (any case) when there is one, else a new person.
    /// A voice named before moves to the new owner.
    /// </summary>
    public async Task<Guid> NameVoiceAsync(string name, string speakerId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var id = await NameVoiceAsync(connection, transaction, name, speakerId, now, ct);
        await transaction.CommitAsync(ct);
        return id;
    }

    /// <summary>As <see cref="NameVoiceAsync(string, string, DateTimeOffset, CancellationToken)"/>, in the caller's transaction.</summary>
    public static async Task<Guid> NameVoiceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string name, string speakerId, DateTimeOffset now, CancellationToken ct)
    {
        var id = await FindOrCreateAsync(connection, transaction, name, now, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into person_voices (speaker_id, person_id, created_at) values (@speakerId, @id, @now)
            on conflict (speaker_id) do update set person_id = excluded.person_id
            """,
            new { speakerId, id, now }, transaction, cancellationToken: ct));
        return id;
    }

    public async Task<Guid> CreateAsync(string name, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var id = await FindOrCreateAsync(connection, transaction, name, now, ct);
        await transaction.CommitAsync(ct);
        return id;
    }

    public static async Task<Guid> FindOrCreateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string name, DateTimeOffset now, CancellationToken ct) =>
        await connection.QuerySingleAsync<Guid>(new CommandDefinition(
            """
            with created as (
                insert into people (id, name, created_at) values (@id, @name, @now)
                on conflict (lower(name)) do nothing
                returning id)
            select id from created
            union all
            select id from people where lower(name) = lower(@name)
            limit 1
            """,
            new { id = Guid.NewGuid(), name, now }, transaction, cancellationToken: ct));

    /// <summary>
    /// Renames a person (<paramref name="name"/> null keeps the name) and sets or clears the note
    /// (<paramref name="setNote"/> false keeps it). A name another person already has is refused.
    /// </summary>
    public async Task<PersonWrite> UpdateAsync(Guid id, string? name, bool setNote, string? note, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            return await connection.ExecuteAsync(new CommandDefinition(
                "update people set name = coalesce(cast(@name as text), name), note = case when @setNote then cast(@note as text) else note end where id = @id",
                new { id, name, setNote, note }, cancellationToken: ct)) == 1
                ? PersonWrite.Ok
                : PersonWrite.NotFound;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return PersonWrite.NameTaken;
        }
    }

    /// <summary>Deletes the person and their voice links; the segments fall back to the provider's labels.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from people where id = @id", new { id }, cancellationToken: ct)) == 1;
    }

    public async Task<bool> UnlinkVoiceAsync(Guid id, string speakerId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from person_voices where person_id = @id and speaker_id = @speakerId",
            new { id, speakerId }, cancellationToken: ct)) == 1;
    }

    /// <summary>The voices heard in segments that no person owns and that are not the wearer's, busiest first.</summary>
    public async Task<IReadOnlyList<UnnamedVoice>> UnnamedVoicesAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<UnnamedVoice>(new CommandDefinition(
            $"""
            select s.speaker_id as SpeakerId,
                   (array_agg(s.speaker order by s.started_at desc, s.id desc))[1] as Label,
                   count(*)::int as Segments,
                   max(s.started_at) as LastSeenAt
            from segments s
            where s.speaker_id is not null and s.person_id is null and {SpeakerLabel.IsUser} is not true
              and not exists (select 1 from person_voices pv where pv.speaker_id = s.speaker_id)
            group by s.speaker_id
            order by count(*) desc, max(s.started_at) desc, s.speaker_id
            limit @limit
            """, new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Sets the person of one segment, or clears it (null) so the voice's name shows again.</summary>
    public async Task<SegmentLink> SetSegmentPersonAsync(long segmentId, Guid? personId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            return await connection.ExecuteAsync(new CommandDefinition(
                "update segments set person_id = @personId where id = @segmentId", new { segmentId, personId }, cancellationToken: ct)) == 1
                ? SegmentLink.Ok
                : SegmentLink.NoSegment;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return SegmentLink.NoPerson;
        }
    }

    /// <summary>Moves every voice, segment link, voiceprint and fact of <paramref name="id"/> to <paramref name="intoId"/> and deletes <paramref name="id"/>.</summary>
    public async Task<PersonWrite> MergeAsync(Guid id, Guid intoId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var found = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*)::int from people where id in (@id, @intoId)", new { id, intoId }, transaction, cancellationToken: ct));
        if (found != 2)
        {
            return PersonWrite.NotFound;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update person_voices set person_id = @intoId where person_id = @id", new { id, intoId }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "update segments set person_id = @intoId where person_id = @id", new { id, intoId }, transaction, cancellationToken: ct));
        await VoiceGroupStore.MergeVoiceprintAsync(connection, transaction, id, intoId, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update voice_matches set person_id = @intoId
            where person_id = @id and not exists (
                select 1 from voice_matches m where m.conversation_id = voice_matches.conversation_id and m.person_id = @intoId)
            """,
            new { id, intoId }, transaction, cancellationToken: ct));
        // A fact the target already holds (a deleted one too) keeps the target's row.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from person_facts f
            where f.person_id = @id
              and exists (select 1 from person_facts t where t.person_id = @intoId and t.fingerprint = f.fingerprint)
            """,
            new { id, intoId }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "update person_facts set person_id = @intoId where person_id = @id", new { id, intoId }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from people where id = @id", new { id }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return PersonWrite.Ok;
    }
}
