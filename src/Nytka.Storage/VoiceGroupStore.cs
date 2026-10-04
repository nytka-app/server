using Dapper;
using Npgsql;
using Nytka.Audio.Voice;

namespace Nytka.Storage;

/// <summary>A fingerprint the grouping job has not looked at yet; <paramref name="Vector"/> never leaves the server.</summary>
public sealed record UngroupedFingerprint(long SegmentId, Guid ConversationId, float[] Vector);

/// <summary>A group's centroid, or a person's voiceprint (<paramref name="Id"/> is then the person), to compare fingerprints with.</summary>
public sealed record VoiceCentroid(Guid Id, float[] Vector);

/// <summary>What confirming a group or a match did.</summary>
public enum VoiceConfirm { Ok, NotFound, NoPerson }

/// <summary>One fingerprinted segment of someone else, for evaluating grouping: ids and numbers, no text and no vector.</summary>
public sealed record VoiceEvalRow(
    long SegmentId, Guid ConversationId, DateTime StartedAt, int DurationMs, Guid? GroupId, Guid? PersonId, Guid? MatchPersonId, float? Similarity);

/// <summary>A group ("Who is this?") or a pending match ("Is this Olena?") a card may be made from; <paramref name="Id"/> is the group or the match.</summary>
public sealed record CardOwner(string Kind, Guid Id, Guid? PersonId, string? PersonName, float? Similarity);

/// <summary>
/// A segment of a card owner that a clip may hold: not the wearer's, no person yet, speech audio still stored.
/// <paramref name="Ordinal"/> is its place among all segments of its conversation, so two segments of an owner with
/// consecutive ordinals have no other speaker's segment between them.
/// </summary>
public sealed record CardSegment(
    string Kind, Guid OwnerId, Guid ConversationId, string? ConversationTitle, DateTime ConversationStartedAt, long SegmentId, int Ordinal,
    DateTime StartedAt, DateTime EndedAt, string Text);

/// <summary>
/// Voice groups, person voiceprints and voice matches (docs/specs/people.md, Layer 2). A vector never leaves Postgres
/// through this store except to be compared: results carry ids, counts and similarities.
/// </summary>
public sealed class VoiceGroupStore(NpgsqlDataSource dataSource)
{
    public const string GroupKind = "group";
    public const string MatchKind = "match";

    /// <summary>
    /// The owners cards may be made from, after the filters <c>@kind</c>, <c>@id</c> (each may be null) and <c>@visibleAt</c>
    /// (null counts skipped ones too): every group, and every pending match whose person still exists.
    /// </summary>
    private const string CardOwners =
        """
        owners as (
            select 'group'::text as kind, g.id as id, cast(null as uuid) as person_id, cast(null as text) as person_name,
                   cast(null as real) as similarity
            from voice_groups g
            where (cast(@kind as text) is null or @kind = 'group') and (cast(@id as uuid) is null or g.id = @id)
              and (cast(@visibleAt as timestamptz) is null or g.skipped_until is null or g.skipped_until <= @visibleAt)
            union all
            select 'match'::text, m.id, m.person_id, p.name, m.similarity
            from voice_matches m join people p on p.id = m.person_id
            where m.status = 'pending' and (cast(@kind as text) is null or @kind = 'match') and (cast(@id as uuid) is null or m.id = @id)
              and (cast(@visibleAt as timestamptz) is null or m.skipped_until is null or m.skipped_until <= @visibleAt))
        """;

    /// <summary>
    /// SQL, after <see cref="SpeakerLabel.Joins"/>, for a segment aliased <c>s</c> that grouping may take: not the wearer's and
    /// with no person.
    /// </summary>
    private const string Eligible = $"{SpeakerLabel.IsUser} is not true and {SpeakerLabel.PersonId} is null";

    private sealed record FingerprintRow(long SegmentId, Guid ConversationId, byte[] Fingerprint);

    private sealed record CentroidRow(Guid Id, byte[] Centroid);

    private sealed record VectorRow(byte[] Centroid, int Count, string Model);

    private sealed record HeldRow(long SegmentId, byte[] Fingerprint, string Model);

    private sealed record MatchRow(Guid PersonId, Array SegmentIds);

    private sealed record PrintRow(Guid PersonId, string Model, byte[] Centroid, int Count);

    private sealed record EvalRow(
        long SegmentId, Guid ConversationId, DateTime StartedAt, int DurationMs, Guid? GroupId, Guid? PersonId, Guid? MatchPersonId,
        string Model, byte[] Fingerprint, string? GroupModel, byte[]? GroupCentroid, string? PrintModel, byte[]? PrintCentroid);

    /// <summary>Whether a fingerprint of <paramref name="model"/> waits for the grouping job.</summary>
    public async Task<bool> HasUngroupedAsync(string model, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"""
            select exists (
                select 1 from segment_fingerprints f join segments s on s.id = f.segment_id {SpeakerLabel.Joins}
                where not f.grouped and f.model = @model and {Eligible})
            """,
            new { model }, cancellationToken: ct));
    }

    /// <summary>The next <paramref name="limit"/> fingerprints grouping may take, in segment order.</summary>
    public async Task<IReadOnlyList<UngroupedFingerprint>> UngroupedAsync(string model, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<FingerprintRow>(new CommandDefinition(
            $"""
            select f.segment_id as SegmentId, s.conversation_id as ConversationId, f.fingerprint as Fingerprint
            from segment_fingerprints f join segments s on s.id = f.segment_id {SpeakerLabel.Joins}
            where not f.grouped and f.model = @model and {Eligible}
            order by f.segment_id
            limit @limit
            """,
            new { model, limit }, cancellationToken: ct));
        return rows.Select(r => new UngroupedFingerprint(r.SegmentId, r.ConversationId, VoiceStore.Decode(r.Fingerprint))).ToList();
    }

    public Task<IReadOnlyList<VoiceCentroid>> GroupsAsync(string model, CancellationToken ct) =>
        CentroidsAsync("select id as Id, centroid as Centroid from voice_groups where model = @model", model, ct);

    public Task<IReadOnlyList<VoiceCentroid>> VoiceprintsAsync(string model, CancellationToken ct) =>
        CentroidsAsync("select person_id as Id, centroid as Centroid from person_voiceprints where model = @model", model, ct);

    private async Task<IReadOnlyList<VoiceCentroid>> CentroidsAsync(string sql, string model, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<CentroidRow>(new CommandDefinition(sql, new { model }, cancellationToken: ct));
        return rows.Select(r => new VoiceCentroid(r.Id, VoiceStore.Decode(r.Centroid))).ToList();
    }

    /// <summary>
    /// Adds the fingerprint of <paramref name="segmentId"/> to the group, moving its centroid as a running mean, and marks it
    /// grouped. Returns the new centroid, or null when the group or the fingerprint is gone.
    /// </summary>
    public async Task<float[]?> JoinGroupAsync(long segmentId, Guid groupId, float[] vector, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var group = await connection.QuerySingleOrDefaultAsync<VectorRow>(new CommandDefinition(
            "select centroid as Centroid, count as Count, model as Model from voice_groups where id = @groupId for update",
            new { groupId }, transaction, cancellationToken: ct));
        if (group is null
            || await connection.ExecuteAsync(new CommandDefinition(
                "update segment_fingerprints set group_id = @groupId, grouped = true where segment_id = @segmentId",
                new { segmentId, groupId }, transaction, cancellationToken: ct)) == 0)
        {
            return null;
        }

        var centroid = VoiceStore.Blend(VoiceStore.Decode(group.Centroid), group.Count, [vector]);
        await connection.ExecuteAsync(new CommandDefinition(
            "update voice_groups set centroid = @centroid, count = count + 1, updated_at = @now where id = @groupId",
            new { groupId, centroid = VoiceStore.Encode(centroid), now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return centroid;
    }

    /// <summary>Starts a group of one fingerprint and marks it grouped. Returns the group, or null when the fingerprint is gone.</summary>
    public async Task<VoiceCentroid?> StartGroupAsync(long segmentId, string model, float[] vector, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var id = Guid.NewGuid();
        await connection.ExecuteAsync(new CommandDefinition(
            "insert into voice_groups (id, model, centroid, count, created_at, updated_at) values (@id, @model, @centroid, 1, @now, @now)",
            new { id, model, centroid = VoiceStore.Encode(vector), now }, transaction, cancellationToken: ct));
        if (await connection.ExecuteAsync(new CommandDefinition(
                "update segment_fingerprints set group_id = @id, grouped = true where segment_id = @segmentId",
                new { id, segmentId }, transaction, cancellationToken: ct)) == 0)
        {
            return null;
        }

        await transaction.CommitAsync(ct);
        return new VoiceCentroid(id, vector);
    }

    /// <summary>
    /// Adds the segment to the person's pending match for its conversation, keeping the best similarity, and marks the
    /// fingerprint grouped. A match already decided is left as it is: a rejected one stays rejected. No label changes.
    /// </summary>
    public async Task AddToMatchAsync(
        long segmentId, Guid conversationId, Guid personId, float similarity, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into voice_matches (id, conversation_id, person_id, segment_ids, similarity, created_at)
            values (@id, @conversationId, @personId, array[@segmentId], @similarity, @now)
            on conflict (conversation_id, person_id) do update set
                segment_ids = case when voice_matches.status = 'pending' and not (@segmentId = any(voice_matches.segment_ids))
                                   then array_append(voice_matches.segment_ids, @segmentId) else voice_matches.segment_ids end,
                similarity = case when voice_matches.status = 'pending'
                                  then greatest(voice_matches.similarity, excluded.similarity) else voice_matches.similarity end
            """,
            new { id = Guid.NewGuid(), conversationId, personId, segmentId, similarity, now }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "update segment_fingerprints set grouped = true where segment_id = @segmentId",
            new { segmentId }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>Deletes the groups no fingerprint is in, inside the caller's transaction. A group never outlives its last fingerprint.</summary>
    public static Task DeleteEmptyGroupsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "delete from voice_groups g where not exists (select 1 from segment_fingerprints f where f.group_id = g.id)",
            transaction: transaction, cancellationToken: ct));

    /// <summary>
    /// A person names the group: its segments get the person, the fingerprints still held are blended into the person's
    /// voiceprint (weighted by count, replacing one made by another model) and the group goes, in one transaction.
    /// </summary>
    public async Task<VoiceConfirm> ConfirmGroupAsync(Guid groupId, Guid personId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var result = await ConfirmGroupAsync(connection, transaction, groupId, personId, now, ct);
        if (result == VoiceConfirm.Ok)
        {
            await transaction.CommitAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// As <see cref="ConfirmGroupAsync(Guid, Guid, DateTimeOffset, CancellationToken)"/> for a person found or created by
    /// <paramref name="name"/> (any case), in one transaction: a group that is gone leaves no new person behind.
    /// </summary>
    public async Task<(VoiceConfirm Result, Guid? PersonId)> NameGroupAsync(Guid groupId, string name, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var personId = await PeopleStore.FindOrCreateAsync(connection, transaction, name, now, ct);
        var result = await ConfirmGroupAsync(connection, transaction, groupId, personId, now, ct);
        if (result != VoiceConfirm.Ok)
        {
            return (result, null);
        }

        await transaction.CommitAsync(ct);
        return (result, personId);
    }

    /// <summary>As <see cref="ConfirmGroupAsync(Guid, Guid, DateTimeOffset, CancellationToken)"/>, in the caller's transaction, which the caller commits.</summary>
    public static async Task<VoiceConfirm> ConfirmGroupAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid groupId, Guid personId, DateTimeOffset now, CancellationToken ct)
    {
        var group = await connection.QuerySingleOrDefaultAsync<VectorRow>(new CommandDefinition(
            "select centroid as Centroid, count as Count, model as Model from voice_groups where id = @groupId for update",
            new { groupId }, transaction, cancellationToken: ct));
        if (group is null)
        {
            return VoiceConfirm.NotFound;
        }

        var held = await connection.QueryAsync<HeldRow>(new CommandDefinition(
            "select segment_id as SegmentId, fingerprint as Fingerprint, model as Model from segment_fingerprints where group_id = @groupId",
            new { groupId }, transaction, cancellationToken: ct));
        var result = await LinkAsync(connection, transaction, personId, group.Model, held.ToList(), [], now, ct);
        if (result == VoiceConfirm.Ok)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from voice_groups where id = @groupId", new { groupId }, transaction, cancellationToken: ct));
        }

        return result;
    }

    /// <summary>
    /// A pending match is accepted: its segments get the person and the fingerprints still held join the person's voiceprint;
    /// with their audio gone it only links. <see cref="VoiceConfirm.NotFound"/> when the match is not pending.
    /// </summary>
    public async Task<VoiceConfirm> ConfirmMatchAsync(Guid matchId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var match = await connection.QuerySingleOrDefaultAsync<MatchRow>(new CommandDefinition(
            "select person_id as PersonId, segment_ids as SegmentIds from voice_matches where id = @matchId and status = 'pending' for update",
            new { matchId }, transaction, cancellationToken: ct));
        if (match is null)
        {
            return VoiceConfirm.NotFound;
        }

        var segmentIds = match.SegmentIds.Cast<long>().ToArray();
        var held = (await connection.QueryAsync<HeldRow>(new CommandDefinition(
            "select segment_id as SegmentId, fingerprint as Fingerprint, model as Model from segment_fingerprints where segment_id = any(@ids)",
            new { ids = segmentIds }, transaction, cancellationToken: ct))).ToList();
        var result = await LinkAsync(
            connection, transaction, match.PersonId, held.FirstOrDefault()?.Model, held, segmentIds.Except(held.Select(h => h.SegmentId)).ToList(), now, ct);
        if (result == VoiceConfirm.Ok)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "update voice_matches set status = 'accepted', decided_at = @now where id = @matchId",
                new { matchId, now }, transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// Sets the person on the segments of <paramref name="held"/> and <paramref name="others"/> (those without a fingerprint)
    /// that have none, and blends the held vectors of <paramref name="model"/> into the person's voiceprint.
    /// </summary>
    private static async Task<VoiceConfirm> LinkAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid personId, string? model, IReadOnlyList<HeldRow> held,
        IReadOnlyList<long> others, DateTimeOffset now, CancellationToken ct)
    {
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from people where id = @personId)", new { personId }, transaction, cancellationToken: ct)))
        {
            return VoiceConfirm.NoPerson;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update segments set person_id = @personId where id = any(@ids) and person_id is null",
            new { personId, ids = held.Select(h => h.SegmentId).Concat(others).ToArray() }, transaction, cancellationToken: ct));

        var vectors = held.Where(h => h.Model == model).Select(h => VoiceStore.Decode(h.Fingerprint)).ToList();
        if (model is not null && vectors.Count > 0)
        {
            var current = await connection.QuerySingleOrDefaultAsync<VectorRow>(new CommandDefinition(
                "select centroid as Centroid, count as Count, model as Model from person_voiceprints where person_id = @personId for update",
                new { personId }, transaction, cancellationToken: ct));
            var same = current is not null && current.Model == model;
            var centroid = same ? VoiceStore.Blend(VoiceStore.Decode(current!.Centroid), current.Count, vectors) : VoiceStore.Mean(vectors);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into person_voiceprints (person_id, model, centroid, count, updated_at)
                values (@personId, @model, @centroid, @count, @now)
                on conflict (person_id) do update set
                    model = excluded.model, centroid = excluded.centroid, count = excluded.count, updated_at = excluded.updated_at
                """,
                new { personId, model, centroid = VoiceStore.Encode(centroid), count = (same ? current!.Count : 0) + vectors.Count, now },
                transaction, cancellationToken: ct));
        }

        return VoiceConfirm.Ok;
    }

    /// <summary>
    /// Moves the voiceprint of <paramref name="fromId"/> to <paramref name="intoId"/>, inside the caller's transaction: blended
    /// by count when both have one of the same model, left to the target when the models differ, moved over when the target has none.
    /// </summary>
    public static async Task MergeVoiceprintAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid fromId, Guid intoId, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<PrintRow>(new CommandDefinition(
            """
            select person_id as PersonId, model as Model, centroid as Centroid, count as Count
            from person_voiceprints where person_id in (@fromId, @intoId) for update
            """,
            new { fromId, intoId }, transaction, cancellationToken: ct))).ToList();
        var from = rows.FirstOrDefault(r => r.PersonId == fromId);
        var into = rows.FirstOrDefault(r => r.PersonId == intoId);
        if (from is null)
        {
            return;
        }

        if (into is null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "update person_voiceprints set person_id = @intoId where person_id = @fromId",
                new { fromId, intoId }, transaction, cancellationToken: ct));
        }
        else if (from.Model == into.Model)
        {
            var centroid = VoiceStore.Blend(VoiceStore.Decode(into.Centroid), into.Count, VoiceStore.Decode(from.Centroid), from.Count);
            await connection.ExecuteAsync(new CommandDefinition(
                "update person_voiceprints set centroid = @centroid, count = count + @count, updated_at = now() where person_id = @intoId",
                new { intoId, centroid = VoiceStore.Encode(centroid), count = from.Count }, transaction, cancellationToken: ct));
        }
    }

    /// <summary>
    /// What cards are made from: the owners not skipped at <paramref name="now"/> and their segments (still unnamed, with speech
    /// audio stored), in segment order. Only the ones with at least one segment are of use.
    /// </summary>
    public Task<(IReadOnlyList<CardOwner> Owners, IReadOnlyList<CardSegment> Segments)> CardInputAsync(DateTimeOffset now, CancellationToken ct) =>
        ReadCardInputAsync(null, null, now, ct);

    /// <summary>As <see cref="CardInputAsync(DateTimeOffset, CancellationToken)"/> for one owner, skipped or not.</summary>
    public Task<(IReadOnlyList<CardOwner> Owners, IReadOnlyList<CardSegment> Segments)> CardInputAsync(string kind, Guid id, CancellationToken ct) =>
        ReadCardInputAsync(kind, id, null, ct);

    private async Task<(IReadOnlyList<CardOwner>, IReadOnlyList<CardSegment>)> ReadCardInputAsync(
        string? kind, Guid? id, DateTimeOffset? visibleAt, CancellationToken ct)
    {
        var args = new { kind, id, visibleAt };
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var owners = await connection.QueryAsync<CardOwner>(new CommandDefinition(
            $"""
            with {CardOwners}
            select kind as Kind, id as Id, person_id as PersonId, person_name as PersonName, similarity as Similarity from owners
            """,
            args, cancellationToken: ct));
        var segments = await connection.QueryAsync<CardSegment>(new CommandDefinition(
            $"""
            with {CardOwners},
            members as (
                select o.kind, o.id as owner, f.segment_id as segment_id
                from owners o join segment_fingerprints f on o.kind = 'group' and f.group_id = o.id
                union all
                select o.kind, o.id, sid
                from owners o join voice_matches m on o.kind = 'match' and m.id = o.id
                cross join lateral unnest(m.segment_ids) as sid),
            ranked as (
                select s.id, row_number() over (partition by s.conversation_id order by s.started_at, s.id) as ord
                from segments s
                where s.conversation_id in (select s2.conversation_id from segments s2 join members mm on mm.segment_id = s2.id))
            select m.kind as Kind, m.owner as OwnerId, s.conversation_id as ConversationId,
                   coalesce(c.title, c.ai_title) as ConversationTitle, c.started_at as ConversationStartedAt, s.id as SegmentId,
                   r.ord::int as Ordinal, s.started_at as StartedAt, s.ended_at as EndedAt, s.text as Text
            from members m
            join segments s on s.id = m.segment_id {SpeakerLabel.Joins}
            join ranked r on r.id = s.id
            join conversations c on c.id = s.conversation_id
            where {Eligible}
              and exists (select 1 from speech_audio a where a.batch_id = s.batch_id and a.started_at < s.ended_at and a.ended_at > s.started_at)
            order by s.conversation_id, r.ord
            """,
            args, cancellationToken: ct));
        return (owners.ToList(), segments.ToList());
    }

    /// <summary>The person a pending match asks about, or null when the match is not pending.</summary>
    public async Task<Guid?> MatchPersonAsync(Guid matchId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select person_id from voice_matches where id = @matchId and status = 'pending'", new { matchId }, cancellationToken: ct));
    }

    /// <summary>Hides a group, or a pending match, until <paramref name="until"/>. False when there is none.</summary>
    public async Task<bool> SkipAsync(string kind, Guid id, DateTimeOffset until, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            kind == GroupKind
                ? "update voice_groups set skipped_until = @until where id = @id"
                : "update voice_matches set skipped_until = @until where id = @id and status = 'pending'",
            new { id, until }, cancellationToken: ct)) == 1;
    }

    /// <summary>
    /// "Not a person" for a group, "not them" for a pending match. A group is deleted (its fingerprints stay marked grouped, so
    /// they are never grouped again); a match is kept as rejected, so it never comes back. False when there is none.
    /// </summary>
    public async Task<bool> RejectCardAsync(string kind, Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            kind == GroupKind
                ? "delete from voice_groups where id = @id"
                : "update voice_matches set status = 'rejected', decided_at = @now where id = @id and status = 'pending'",
            new { id, now }, cancellationToken: ct)) == 1;
    }

    /// <summary>Every group, every person voiceprint and every pending match, in one transaction. Segment links stay: they are statements.</summary>
    public async Task ForgetAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from voice_groups;
            delete from person_voiceprints;
            delete from voice_matches where status = 'pending';
            """,
            transaction: transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Fingerprinted segments of other people that started after <paramref name="since"/> and before <paramref name="until"/>,
    /// oldest first, with the group and person they ended in and their similarity to the match's voiceprint, else to the group's centroid.
    /// </summary>
    public async Task<IReadOnlyList<VoiceEvalRow>> EvalAsync(DateTimeOffset? since, DateTimeOffset? until, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<EvalRow>(new CommandDefinition(
            $"""
            select s.id as SegmentId, s.conversation_id as ConversationId, s.started_at as StartedAt,
                   (extract(epoch from s.ended_at - s.started_at) * 1000)::int as DurationMs,
                   f.group_id as GroupId, {SpeakerLabel.PersonId} as PersonId, m.person_id as MatchPersonId,
                   f.model as Model, f.fingerprint as Fingerprint,
                   g.model as GroupModel, g.centroid as GroupCentroid, vp.model as PrintModel, vp.centroid as PrintCentroid
            from segment_fingerprints f
            join segments s on s.id = f.segment_id {SpeakerLabel.Joins}
            left join voice_groups g on g.id = f.group_id
            left join lateral (
                select vm.person_id from voice_matches vm
                where vm.conversation_id = s.conversation_id and s.id = any(vm.segment_ids)
                order by vm.similarity desc limit 1) m on true
            left join person_voiceprints vp on vp.person_id = m.person_id
            where {SpeakerLabel.IsUser} is not true
              and (cast(@since as timestamptz) is null or s.started_at > cast(@since as timestamptz))
              and (cast(@until as timestamptz) is null or s.started_at < cast(@until as timestamptz))
            order by s.started_at, s.id
            limit @limit
            """,
            new { since, until, limit }, cancellationToken: ct));
        return rows.Select(r => new VoiceEvalRow(
            r.SegmentId, r.ConversationId, r.StartedAt, r.DurationMs, r.GroupId, r.PersonId, r.MatchPersonId, Similarity(r))).ToList();
    }

    private static float? Similarity(EvalRow row)
    {
        var (model, centroid) = row.PrintCentroid is not null ? (row.PrintModel, row.PrintCentroid) : (row.GroupModel, row.GroupCentroid);
        if (centroid is null || model != row.Model)
        {
            return null;
        }

        float[] fingerprint = VoiceStore.Decode(row.Fingerprint), other = VoiceStore.Decode(centroid);
        return fingerprint.Length == other.Length ? SpeakerEmbedder.Cosine(fingerprint, other) : null;
    }
}
