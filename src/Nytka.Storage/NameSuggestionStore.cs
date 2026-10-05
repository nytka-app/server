using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// A segment as name suggestion reads it. <see cref="Label"/> is what the label rule gives it; <see cref="Unnamed"/> is
/// true when it is not the wearer's and has no person, and then <see cref="SpeakerId"/>, <see cref="BatchId"/> and
/// <see cref="Speaker"/> (the provider's own label) decide its target. <see cref="IsWearer"/> is the label rule's verdict.
/// <see cref="PersonId"/> is the person the segment belongs to and <see cref="PersonNamed"/> whether they have a name;
/// <see cref="RoleOnly"/> is true for a person known only by role, who is still a target for a name (docs/specs/tags.md, Roles).
/// </summary>
public sealed record NameSegment(
    long Id, DateTime StartedAt, long BatchId, string? Speaker, string? SpeakerId, string? Label, string Text, bool Unnamed,
    bool IsWearer, Guid? PersonId = null, bool PersonNamed = true, string? SpeechKind = null)
{
    /// <summary>Whether the speech kind that applies is media: such a line is no target and no evidence (docs/specs/speech-kind.md).</summary>
    public bool IsMedia => SpeechKind == Storage.SpeechKinds.Media;

    public bool RoleOnly => !IsWearer && PersonId is not null && !PersonNamed;
}

public sealed record NameInput(string? Title, DateTime StartedAt, IReadOnlyList<NameSegment> Segments)
{
    public long? LastSegmentId => Segments.Count == 0 ? null : Segments.Max(s => s.Id);
}

/// <summary>
/// A name, a role or both that the model proposed for one target, ready to store. A role with no name stores the role's display
/// form as <paramref name="Name"/> and <paramref name="Named"/> false. For target <c>person</c>, <paramref name="PersonId"/> is the
/// person known only by role whom the name is for.
/// </summary>
public sealed record NameCandidate(
    string Target, string? SpeakerId, long[] SegmentIds, string Name, Guid? PersonId, long EvidenceSegmentId, float Confidence,
    string? Role = null, bool Named = true);

public sealed record NameSuggestionRow(
    Guid Id, Guid ConversationId, string Target, string? SpeakerId, Guid? GroupId, string Name, string? Role, bool Named, Guid? PersonId,
    float Confidence, NameEvidence Evidence, int SameName = 1);

public sealed record NameEvidence(long SegmentId, DateTime StartedAt, string Text);

public sealed record PersonName(Guid Id, string Name);

public sealed record NameRun(string Status, long? ThroughSegmentId, int Failures, int Validator);

/// <summary>A pending suggestion as revalidation reads it, with the text of its evidence segment.</summary>
public sealed record PendingName(Guid Id, Guid ConversationId, string Name, string? Role, bool Named, string EvidenceText);

public enum SuggestionDecision { Ok, NotFound, NotPending, Skipped }

public enum AcceptByName { Ok, NotFound, Conflict }

/// <summary>What accepting every pending suggestion for one name did: the person, how many were applied and how many could not be.</summary>
public sealed record AcceptByNameResult(AcceptByName Result, Guid? PersonId = null, int Accepted = 0, int Skipped = 0);

/// <summary>Name suggestions (<c>name_suggestions</c>) and the record of the runs that make them (<c>people_runs</c>, kind <c>names</c>).</summary>
public sealed class NameSuggestionStore(NpgsqlDataSource dataSource)
{
    public const string Names = "names";

    private sealed record Header(string? Title, DateTime StartedAt);

    private sealed record RunMark(long? Through, int Validator);

    private sealed record Row(
        Guid Id, Guid ConversationId, string Target, string? SpeakerId, Guid? GroupId, string Name, string? Role, bool Named, Guid? PersonId,
        float Confidence, long EvidenceSegmentId, DateTime EvidenceStartedAt, string EvidenceText, int SameName);

    // A class, not a record: Npgsql reports an int8[] column as System.Array, which a constructor parameter of long[] does not match.
    private sealed class Pending
    {
        public Guid Id { get; init; }

        public string Target { get; init; } = "";

        public string? SpeakerId { get; init; }

        public Guid? GroupId { get; init; }

        public long[] SegmentIds { get; init; } = [];

        public string Name { get; init; } = "";

        public string? Role { get; init; }

        public bool Named { get; init; }

        public Guid? PersonId { get; init; }

        public string Status { get; init; } = "";
    }

    /// <summary>The conversation's title, day and segments, or null when it is gone. Reads inside <paramref name="transaction"/> when given.</summary>
    public async Task<NameInput?> ReadInputAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid conversationId, CancellationToken ct)
    {
        var header = await connection.QuerySingleOrDefaultAsync<Header>(new CommandDefinition(
            "select coalesce(title, ai_title) as Title, started_at as StartedAt from conversations where id = @conversationId",
            new { conversationId }, transaction, cancellationToken: ct));
        if (header is null)
        {
            return null;
        }

        var rows = await connection.QueryAsync<NameSegment>(new CommandDefinition(
            $"""
            select s.id as Id, s.started_at as StartedAt, s.batch_id as BatchId, s.speaker as Speaker, s.speaker_id as SpeakerId,
                   {SpeakerLabel.Column} as Label, s.text as Text,
                   ({SpeakerLabel.IsUser} is not true and {SpeakerLabel.PersonId} is null) as Unnamed,
                   coalesce({SpeakerLabel.IsUser}, false) as IsWearer,
                   {SpeakerLabel.PersonId} as PersonId, {SpeakerLabel.PersonNamed} as PersonNamed, s.speech_kind as SpeechKind
            from segments s {SpeakerLabel.Joins}
            where s.conversation_id = @conversationId
            order by s.started_at, s.id
            """,
            new { conversationId }, transaction, cancellationToken: ct));
        return new NameInput(header.Title, header.StartedAt, rows.ToList());
    }

    public async Task<NameInput?> ReadInputAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await ReadInputAsync(connection, null, conversationId, ct);
    }

    /// <summary>The names of every named person, with their ids: a suggestion equal to one carries that person. A person known only by role is not one.</summary>
    public async Task<IReadOnlyDictionary<string, PersonName>> PeopleByNameAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var people = await connection.QueryAsync<PersonName>(new CommandDefinition(
            "select id as Id, name as Name from people where named", cancellationToken: ct));
        return people.ToDictionary(p => p.Name.ToLowerInvariant(), p => p);
    }

    public async Task<NameRun?> GetRunAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<NameRun>(new CommandDefinition(
            """
            select status as Status, through_segment_id as ThroughSegmentId, failures as Failures, validator as Validator
            from people_runs where conversation_id = @conversationId and kind = 'names'
            """,
            new { conversationId }, cancellationToken: ct));
    }

    /// <summary>
    /// Records that a run is queued, inside the transaction of the summary that asked for it, and returns whether the caller
    /// should queue the job. False when the last run already read every segment under the current <paramref name="validator"/>
    /// version. The failure count starts again only when segments arrived after that run.
    /// </summary>
    public async Task<bool> MarkPendingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, int validator, DateTimeOffset now,
        CancellationToken ct)
    {
        var last = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select max(id) from segments where conversation_id = @conversationId",
            new { conversationId }, transaction, cancellationToken: ct));
        var run = await connection.QuerySingleOrDefaultAsync<RunMark>(new CommandDefinition(
            "select through_segment_id as Through, validator as Validator from people_runs where conversation_id = @conversationId and kind = 'names' for update",
            new { conversationId }, transaction, cancellationToken: ct));
        if (last is null || run is { Through: { } through } && through >= last && run.Validator >= validator)
        {
            return false;
        }

        var fresh = run?.Through is not null;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into people_runs (conversation_id, kind, status, updated_at) values (@conversationId, 'names', 'pending', @now)
            on conflict (conversation_id, kind) do update
            set status = 'pending', message = null, updated_at = @now,
                failures = case when @fresh then 0 else people_runs.failures end
            """,
            new { conversationId, now, fresh }, transaction, cancellationToken: ct));
        return true;
    }

    /// <summary>Drops the record of a run that will not happen (suggesting is off or has no model), so it never stays pending.</summary>
    public async Task ForgetRunAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from people_runs where conversation_id = @conversationId and kind = 'names' and status = 'pending'",
            new { conversationId }, cancellationToken: ct));
    }

    /// <summary>
    /// Stores the candidates and records the run as done in one transaction; a name already stored for the target, a rejected
    /// one included, is skipped. The run records the <paramref name="validator"/> version it applied. Returns the number
    /// inserted, or null when the conversation is gone.
    /// </summary>
    public async Task<int?> ApplyAsync(
        Guid conversationId, IReadOnlyList<NameCandidate> candidates, long? through, int validator, DateTimeOffset now,
        CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // The conversation may have been deleted while the model worked; its suggestions go with it.
        if (await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "select id from conversations where id = @conversationId for share",
                new { conversationId }, transaction, cancellationToken: ct)) is null)
        {
            return null;
        }

        var inserted = 0;
        foreach (var candidate in candidates)
        {
            inserted += await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into name_suggestions (id, conversation_id, target, speaker_id, segment_ids, name, role, named, person_id,
                                              evidence_segment_id, confidence, created_at)
                values (@id, @conversationId, @target, @speakerId, @segmentIds, @name, @role, @named, @personId, @evidence, @confidence, @now)
                on conflict do nothing
                """,
                new
                {
                    id = Guid.CreateVersion7(now), conversationId, target = candidate.Target, speakerId = candidate.SpeakerId,
                    segmentIds = candidate.SegmentIds, name = candidate.Name, role = candidate.Role, named = candidate.Named, personId = candidate.PersonId,
                    evidence = candidate.EvidenceSegmentId, confidence = candidate.Confidence, now,
                }, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into people_runs (conversation_id, kind, status, through_segment_id, validator, updated_at)
            values (@conversationId, 'names', 'done', @through, @validator, @now)
            on conflict (conversation_id, kind) do update
            set status = 'done', through_segment_id = @through, validator = @validator, failures = 0, message = null, updated_at = @now
            """,
            new { conversationId, through, validator, now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return inserted;
    }

    /// <summary>Records a run that failed its attempts and returns the failure count; null when the conversation is gone.</summary>
    public async Task<int?> MarkFailedAsync(Guid conversationId, string message, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            insert into people_runs (conversation_id, kind, status, failures, message, updated_at)
            select id, 'names', 'failed', 1, @message, @now from conversations where id = @conversationId
            on conflict (conversation_id, kind) do update
            set status = 'failed', failures = people_runs.failures + 1, message = @message, updated_at = @now
            returning failures
            """,
            new { conversationId, message, now }, cancellationToken: ct));
    }

    /// <summary>Suggestions with the given status, newest first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<NameSuggestionRow>> ListAsync(string status, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            """
            select n.id as Id, n.conversation_id as ConversationId, n.target as Target, n.speaker_id as SpeakerId,
                   n.group_id as GroupId, n.name as Name, n.role as Role, n.named as Named, n.person_id as PersonId, n.confidence as Confidence,
                   e.id as EvidenceSegmentId, e.started_at as EvidenceStartedAt, e.text as EvidenceText,
                   (select count(*)::int from name_suggestions p where p.status = 'pending' and lower(p.name) = lower(n.name)) as SameName
            from name_suggestions n
            join segments e on e.id = n.evidence_segment_id
            where n.status = @status
            order by n.created_at desc, n.id desc
            limit @limit
            """,
            new { status, limit }, cancellationToken: ct));
        return rows.Select(r => new NameSuggestionRow(
            r.Id, r.ConversationId, r.Target, r.SpeakerId, r.GroupId, r.Name, r.Role, r.Named, r.PersonId, r.Confidence,
            new NameEvidence(r.EvidenceSegmentId, r.EvidenceStartedAt, r.EvidenceText), r.SameName)).ToList();
    }

    /// <summary>
    /// Accepts a pending suggestion in one transaction: a <c>speaker</c> names the voice as <c>POST /people</c> does, a
    /// <c>label</c> sets <c>segments.person_id</c> on its segments that have no person yet, a <c>group</c> is named as its card
    /// is (<see cref="VoiceGroupStore.ConfirmGroupAsync(NpgsqlConnection, NpgsqlTransaction, Guid, Guid, DateTimeOffset, CancellationToken)"/>).
    /// A suggestion with a role gives the person the role as a tag. A role with no name (<c>named</c> false) makes a new person
    /// known only by role (<see cref="PeopleStore.CreateNumberedAsync"/>), never one found by name. A <c>person</c> target, the
    /// voice of such a person, renames them, or merges them into the person who already has the name.
    /// Other pending suggestions for the same target are dropped. The result carries the person it named.
    /// </summary>
    public async Task<(SuggestionDecision Result, Guid? PersonId)> AcceptAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var accepted = await AcceptAsync(connection, transaction, id, now, skipTaken: false, ct);
        if (accepted.Result == SuggestionDecision.Ok)
        {
            await transaction.CommitAsync(ct);
        }

        return accepted;
    }

    /// <summary>
    /// Accepts every pending <c>speaker</c>, <c>label</c> and <c>group</c> suggestion of a name that has no role only
    /// (<c>named</c>), matched without case, in one transaction through the same code as <see cref="AcceptAsync(Guid, DateTimeOffset, CancellationToken)"/>,
    /// so the name is one person. A suggestion that can no longer apply is skipped and leaves nothing behind: its voice
    /// already belongs to another person, its segments all have a person, its group is gone, or an earlier accept dropped it.
    /// <see cref="AcceptByName.NotFound"/> when none is pending; <see cref="AcceptByName.Conflict"/> when the pending
    /// suggestions carry different people, or the name is that of a person known only by role.
    /// </summary>
    public async Task<AcceptByNameResult> AcceptByNameAsync(string name, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var pending = (await connection.QueryAsync<(Guid Id, Guid? PersonId)>(new CommandDefinition(
            """
            select id as Id, person_id as PersonId from name_suggestions
            where status = 'pending' and named and target in ('speaker', 'label', 'group') and lower(name) = lower(@name)
            order by created_at, id
            for update
            """,
            new { name }, transaction, cancellationToken: ct))).ToList();
        if (pending.Count == 0)
        {
            return new AcceptByNameResult(AcceptByName.NotFound);
        }

        if (pending.Select(p => p.PersonId).Where(p => p is not null).Distinct().Count() > 1
            || await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from people where lower(name) = lower(@name) and not named)", new { name }, transaction, cancellationToken: ct)))
        {
            return new AcceptByNameResult(AcceptByName.Conflict);
        }

        Guid? person = null;
        var accepted = 0;
        var skipped = 0;
        foreach (var (id, _) in pending)
        {
            await transaction.SaveAsync("suggestion", ct);
            var decision = await AcceptAsync(connection, transaction, id, now, skipTaken: true, ct);
            if (decision.Result == SuggestionDecision.Ok)
            {
                person = decision.PersonId;
                accepted++;
            }
            else
            {
                await transaction.RollbackAsync("suggestion", ct);
                skipped++;
            }
        }

        await transaction.CommitAsync(ct);
        person ??= await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select id from people where lower(name) = lower(@name)", new { name }, cancellationToken: ct));
        return new AcceptByNameResult(AcceptByName.Ok, person, accepted, skipped);
    }

    /// <summary>
    /// The body of both accepts, inside the caller's transaction, which the caller commits on <see cref="SuggestionDecision.Ok"/>.
    /// With <paramref name="skipTaken"/> a suggestion that would change nothing, or take a voice from another person, is
    /// <see cref="SuggestionDecision.Skipped"/>.
    /// </summary>
    private static async Task<(SuggestionDecision Result, Guid? PersonId)> AcceptAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, DateTimeOffset now, bool skipTaken, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<Pending>(new CommandDefinition(
            """
            select id as Id, target as Target, speaker_id as SpeakerId, group_id as GroupId, segment_ids as SegmentIds,
                   name as Name, role as Role, named as Named, person_id as PersonId, status as Status
            from name_suggestions where id = @id for update
            """,
            new { id }, transaction, cancellationToken: ct));
        if (row is null)
        {
            return (SuggestionDecision.NotFound, null);
        }

        if (row.Status != "pending")
        {
            return (SuggestionDecision.NotPending, null);
        }

        Guid person;
        if (row.Target == "person")
        {
            if (row.PersonId is not { } known)
            {
                return (SuggestionDecision.NotFound, null);
            }

            var existing = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "select id from people where lower(name) = lower(@name) and id <> @known", new { row.Name, known }, transaction, cancellationToken: ct));
            if (existing is { } target)
            {
                // The suggestion goes with the merged person (cascade), so there is no status to set.
                await PeopleStore.MergeAsync(connection, transaction, known, target, ct);
                return (SuggestionDecision.Ok, target);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "update people set name = @name, named = true where id = @known", new { row.Name, known }, transaction, cancellationToken: ct));
            person = known;
        }
        else if (row.Target == "speaker")
        {
            if (skipTaken && await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "select exists (select 1 from person_voices v join people p on p.id = v.person_id where v.speaker_id = @speakerId and lower(p.name) <> lower(@name))",
                    new { row.SpeakerId, row.Name }, transaction, cancellationToken: ct)))
            {
                return (SuggestionDecision.Skipped, null);
            }

            person = await PersonAsync(connection, transaction, row, now, ct);
            await PeopleStore.LinkVoiceAsync(connection, transaction, person, row.SpeakerId!, now, ct);
        }
        else if (row.Target == "label")
        {
            if (skipTaken && !await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "select exists (select 1 from segments where id = any(@segmentIds) and person_id is null)",
                    new { segmentIds = row.SegmentIds }, transaction, cancellationToken: ct)))
            {
                return (SuggestionDecision.Skipped, null);
            }

            person = await PersonAsync(connection, transaction, row, now, ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "update segments set person_id = @person where id = any(@segmentIds) and person_id is null",
                new { person, segmentIds = row.SegmentIds }, transaction, cancellationToken: ct));
        }
        else if (row.GroupId is { } groupId)
        {
            person = await PeopleStore.FindOrCreateAsync(connection, transaction, row.Name, now, ct);
            if (await VoiceGroupStore.ConfirmGroupAsync(connection, transaction, groupId, person, now, ct) != VoiceConfirm.Ok)
            {
                return (skipTaken ? SuggestionDecision.Skipped : SuggestionDecision.NotFound, null);
            }

            // The group goes with every suggestion for it, this one included (cascade), so there is no status to set.
            return (SuggestionDecision.Ok, person);
        }
        else
        {
            return (SuggestionDecision.NotFound, null);
        }

        if (row.Role is not null)
        {
            // A person who already holds 20 tags keeps them; the role is not worth refusing the name for.
            await TagStore.AddToPersonAsync(connection, transaction, person, row.Role, now, ct);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update name_suggestions set status = 'accepted', decided_at = @now where id = @id",
            new { id, now }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from name_suggestions
            where status = 'pending' and id <> @id and target = @target
              and coalesce(speaker_id, case when target = 'person' then person_id::text end, segment_ids[1]::text)
                  = coalesce(@speakerId, @personKey::text, @first::text)
            """,
            new { id, row.Target, row.SpeakerId, personKey = row.Target == "person" ? row.PersonId?.ToString() : null, first = row.SegmentIds.Length == 0 ? (long?)null : row.SegmentIds[0] },
            transaction, cancellationToken: ct));
        return (SuggestionDecision.Ok, person);
    }

    /// <summary>The person a suggestion names: the one with that name, or for a role without a name a new, numbered one.</summary>
    private static Task<Guid> PersonAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Pending row, DateTimeOffset now, CancellationToken ct) =>
        row.Named
            ? PeopleStore.FindOrCreateAsync(connection, transaction, row.Name, now, ct)
            : PeopleStore.CreateNumberedAsync(connection, transaction, row.Name, now, ct);

    /// <summary>The pending suggestions the model made (not a voice group's), with the text of their evidence segments.</summary>
    public async Task<IReadOnlyList<PendingName>> PendingFromModelAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<PendingName>(new CommandDefinition(
            """
            select n.id as Id, n.conversation_id as ConversationId, n.name as Name, n.role as Role, n.named as Named, e.text as EvidenceText
            from name_suggestions n join segments e on e.id = n.evidence_segment_id
            where n.status = 'pending' and n.target in ('speaker', 'label', 'person')
            order by n.created_at, n.id
            """, cancellationToken: ct))).ToList();
    }

    /// <summary>Deletes the pending suggestions among <paramref name="ids"/>, so a new run may offer the name again. Returns how many went.</summary>
    public async Task<int> DeletePendingAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from name_suggestions where status = 'pending' and id = any(@ids)", new { ids = ids.ToArray() }, cancellationToken: ct));
    }

    public async Task<SuggestionDecision> RejectAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        if (await connection.ExecuteAsync(new CommandDefinition(
                "update name_suggestions set status = 'rejected', decided_at = @now where id = @id and status = 'pending'",
                new { id, now }, cancellationToken: ct)) == 1)
        {
            return SuggestionDecision.Ok;
        }

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from name_suggestions where id = @id)", new { id }, cancellationToken: ct))
            ? SuggestionDecision.NotPending
            : SuggestionDecision.NotFound;
    }
}
