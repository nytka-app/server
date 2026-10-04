using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// A segment as name suggestion reads it. <see cref="Label"/> is what the label rule gives it; <see cref="Unnamed"/> is
/// true when it is not the wearer's and has no person, and then <see cref="SpeakerId"/>, <see cref="BatchId"/> and
/// <see cref="Speaker"/> (the provider's own label) decide its target.
/// </summary>
public sealed record NameSegment(
    long Id, DateTime StartedAt, long BatchId, string? Speaker, string? SpeakerId, string? Label, string Text, bool Unnamed);

public sealed record NameInput(string? Title, DateTime StartedAt, IReadOnlyList<NameSegment> Segments)
{
    public long? LastSegmentId => Segments.Count == 0 ? null : Segments.Max(s => s.Id);
}

/// <summary>A name the model proposed for one target, ready to store.</summary>
public sealed record NameCandidate(
    string Target, string? SpeakerId, long[] SegmentIds, string Name, Guid? PersonId, long EvidenceSegmentId, float Confidence);

public sealed record NameSuggestionRow(
    Guid Id, Guid ConversationId, string Target, string? SpeakerId, Guid? GroupId, string Name, Guid? PersonId, float Confidence,
    NameEvidence Evidence);

public sealed record NameEvidence(long SegmentId, DateTime StartedAt, string Text);

public sealed record PersonName(Guid Id, string Name);

public sealed record NameRun(string Status, long? ThroughSegmentId, int Failures);

public enum SuggestionDecision { Ok, NotFound, NotPending }

/// <summary>Name suggestions (<c>name_suggestions</c>) and the record of the runs that make them (<c>people_runs</c>, kind <c>names</c>).</summary>
public sealed class NameSuggestionStore(NpgsqlDataSource dataSource)
{
    public const string Names = "names";

    private sealed record Header(string? Title, DateTime StartedAt);

    private sealed record Row(
        Guid Id, Guid ConversationId, string Target, string? SpeakerId, Guid? GroupId, string Name, Guid? PersonId, float Confidence,
        long EvidenceSegmentId, DateTime EvidenceStartedAt, string EvidenceText);

    // A class, not a record: Npgsql reports an int8[] column as System.Array, which a constructor parameter of long[] does not match.
    private sealed class Pending
    {
        public Guid Id { get; init; }

        public string Target { get; init; } = "";

        public string? SpeakerId { get; init; }

        public Guid? GroupId { get; init; }

        public long[] SegmentIds { get; init; } = [];

        public string Name { get; init; } = "";

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
                   ({SpeakerLabel.IsUser} is not true and {SpeakerLabel.PersonId} is null) as Unnamed
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

    /// <summary>The names of every person, with their ids: a suggestion equal to one carries that person.</summary>
    public async Task<IReadOnlyDictionary<string, PersonName>> PeopleByNameAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var people = await connection.QueryAsync<PersonName>(new CommandDefinition(
            "select id as Id, name as Name from people", cancellationToken: ct));
        return people.ToDictionary(p => p.Name.ToLowerInvariant(), p => p);
    }

    public async Task<NameRun?> GetRunAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<NameRun>(new CommandDefinition(
            """
            select status as Status, through_segment_id as ThroughSegmentId, failures as Failures
            from people_runs where conversation_id = @conversationId and kind = 'names'
            """,
            new { conversationId }, cancellationToken: ct));
    }

    /// <summary>
    /// Records that a run is queued, inside the transaction of the summary that asked for it, and returns whether the caller
    /// should queue the job. False when the last run already read every segment. The failure count starts again only
    /// when segments arrived after that run.
    /// </summary>
    public async Task<bool> MarkPendingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, DateTimeOffset now, CancellationToken ct)
    {
        var last = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select max(id) from segments where conversation_id = @conversationId",
            new { conversationId }, transaction, cancellationToken: ct));
        var through = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select through_segment_id from people_runs where conversation_id = @conversationId and kind = 'names' for update",
            new { conversationId }, transaction, cancellationToken: ct));
        if (last is null || through >= last)
        {
            return false;
        }

        var fresh = through is not null;
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
    /// one included, is skipped. Returns the number inserted, or null when the conversation is gone.
    /// </summary>
    public async Task<int?> ApplyAsync(
        Guid conversationId, IReadOnlyList<NameCandidate> candidates, long? through, DateTimeOffset now, CancellationToken ct)
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
                insert into name_suggestions (id, conversation_id, target, speaker_id, segment_ids, name, person_id,
                                              evidence_segment_id, confidence, created_at)
                values (@id, @conversationId, @target, @speakerId, @segmentIds, @name, @personId, @evidence, @confidence, @now)
                on conflict do nothing
                """,
                new
                {
                    id = Guid.CreateVersion7(now), conversationId, target = candidate.Target, speakerId = candidate.SpeakerId,
                    segmentIds = candidate.SegmentIds, name = candidate.Name, personId = candidate.PersonId,
                    evidence = candidate.EvidenceSegmentId, confidence = candidate.Confidence, now,
                }, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into people_runs (conversation_id, kind, status, through_segment_id, updated_at)
            values (@conversationId, 'names', 'done', @through, @now)
            on conflict (conversation_id, kind) do update
            set status = 'done', through_segment_id = @through, failures = 0, message = null, updated_at = @now
            """,
            new { conversationId, through, now }, transaction, cancellationToken: ct));
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
                   n.group_id as GroupId, n.name as Name, n.person_id as PersonId, n.confidence as Confidence,
                   e.id as EvidenceSegmentId, e.started_at as EvidenceStartedAt, e.text as EvidenceText
            from name_suggestions n
            join segments e on e.id = n.evidence_segment_id
            where n.status = @status
            order by n.created_at desc, n.id desc
            limit @limit
            """,
            new { status, limit }, cancellationToken: ct));
        return rows.Select(r => new NameSuggestionRow(
            r.Id, r.ConversationId, r.Target, r.SpeakerId, r.GroupId, r.Name, r.PersonId, r.Confidence,
            new NameEvidence(r.EvidenceSegmentId, r.EvidenceStartedAt, r.EvidenceText))).ToList();
    }

    /// <summary>
    /// Accepts a pending suggestion in one transaction: a <c>speaker</c> names the voice as <c>POST /people</c> does, a
    /// <c>label</c> sets <c>segments.person_id</c> on its segments that have no person yet, a <c>group</c> is named as its card
    /// is (<see cref="VoiceGroupStore.ConfirmGroupAsync(NpgsqlConnection, NpgsqlTransaction, Guid, Guid, DateTimeOffset, CancellationToken)"/>).
    /// Other pending suggestions for the same target are dropped. The result carries the person it named.
    /// </summary>
    public async Task<(SuggestionDecision Result, Guid? PersonId)> AcceptAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<Pending>(new CommandDefinition(
            """
            select id as Id, target as Target, speaker_id as SpeakerId, group_id as GroupId, segment_ids as SegmentIds,
                   name as Name, status as Status
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
        if (row.Target == "speaker")
        {
            person = await PeopleStore.NameVoiceAsync(connection, transaction, row.Name, row.SpeakerId!, now, ct);
        }
        else if (row.Target == "label")
        {
            person = await PeopleStore.FindOrCreateAsync(connection, transaction, row.Name, now, ct);
            await connection.ExecuteAsync(new CommandDefinition(
                "update segments set person_id = @person where id = any(@segmentIds) and person_id is null",
                new { person, segmentIds = row.SegmentIds }, transaction, cancellationToken: ct));
        }
        else if (row.GroupId is { } groupId)
        {
            person = await PeopleStore.FindOrCreateAsync(connection, transaction, row.Name, now, ct);
            if (await VoiceGroupStore.ConfirmGroupAsync(connection, transaction, groupId, person, now, ct) != VoiceConfirm.Ok)
            {
                return (SuggestionDecision.NotFound, null);
            }

            // The group goes with every suggestion for it, this one included (cascade), so there is no status to set.
            await transaction.CommitAsync(ct);
            return (SuggestionDecision.Ok, person);
        }
        else
        {
            return (SuggestionDecision.NotFound, null);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update name_suggestions set status = 'accepted', decided_at = @now where id = @id",
            new { id, now }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from name_suggestions
            where status = 'pending' and id <> @id and target = @target
              and coalesce(speaker_id, segment_ids[1]::text) = coalesce(@speakerId, @first::text)
            """,
            new { id, row.Target, row.SpeakerId, first = row.SegmentIds.Length == 0 ? (long?)null : row.SegmentIds[0] },
            transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return (SuggestionDecision.Ok, person);
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
