using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// One line of a day for the clustering: its fingerprints (a window each; one for a line of 30 s or less), its provider's label
/// within the request that made it (<paramref name="Label"/>, null for none), the label rule's wearer verdict
/// (<paramref name="IsUser"/>) and the track it is in now.
/// </summary>
public sealed record TrackLine(
    long SegmentId, string? Label, DateTime StartedAt, DateTime EndedAt, bool? IsUser, Guid? TrackId, IReadOnlyList<float[]> Prints)
{
    public double Seconds => (EndedAt - StartedAt).TotalSeconds;
}

/// <summary>A manual answer about two lines, <paramref name="SegmentId"/> the lower id.</summary>
public sealed record TrackAnswer(long SegmentId, long OtherSegmentId, bool Same);

/// <summary>A track of the day being rebuilt and what a rebuild keeps of it.</summary>
public sealed record OldTrack(Guid Id, Guid? PersonId, bool Dismissed, DateTime? SkippedUntil);

/// <summary>A track of an earlier day, or of this one, to compare centroids with.</summary>
public sealed record TrackCentroid(Guid Id, DateOnly Day, float[] Vector, double Seconds, Guid? PersonId);

/// <summary>A track as the rebuild stores it. <paramref name="Id"/> is an old track's when the rebuild keeps it.</summary>
public sealed record NewTrack(
    Guid Id, float[] Centroid, double Seconds, Guid? PersonId, bool Dismissed, DateTime? SkippedUntil, IReadOnlyList<long> SegmentIds);

/// <summary>A suggestion to store: <paramref name="PersonId"/> for a voiceprint, <paramref name="OtherTrackId"/> for an earlier track.</summary>
public sealed record NewTrackSuggestion(Guid TrackId, Guid? PersonId, Guid? OtherTrackId, float Similarity);

/// <summary>A pending suggestion on a track, for the list.</summary>
public sealed record TrackSuggestionRow(
    Guid Id, string Kind, Guid? PersonId, string? PersonName, Guid? OtherTrackId, DateOnly? OtherDay, int? OtherOrdinal, float Similarity);

/// <summary>A track for the list; no vector.</summary>
public sealed record TrackRow(
    Guid Id, DateOnly Day, int Ordinal, double Seconds, int Segments, Guid? PersonId, string? PersonName, bool Dismissed,
    IReadOnlyList<TrackSuggestionRow> Suggestions);

public enum TrackDecision { Ok, NotFound, NoPerson }

/// <summary>
/// Speaker tracks (<c>speaker_tracks</c>, docs/explanation/features.md, Speaker tracks): the day's inputs for the clustering,
/// the replacement of a day's tracks, the owner's answers, the suggestions and the applied <c>tracks.enabled</c>. A vector never
/// leaves Postgres through this store except to be compared.
/// </summary>
public sealed class TrackStore(NpgsqlDataSource dataSource)
{
    public const string AnswerSame = "same";
    public const string AnswerDifferent = "different";

    private sealed record LineRow(
        long SegmentId, string? Label, DateTime StartedAt, DateTime EndedAt, bool? IsUser, Guid? TrackId, byte[] Fingerprint);

    private sealed record CentroidRow(Guid Id, DateOnly Day, byte[] Centroid, double Seconds, Guid? PersonId);

    private sealed record ListRow(Guid Id, DateOnly Day, int Ordinal, double Seconds, int Segments, Guid? PersonId, string? PersonName, bool Dismissed);

    private sealed record SuggestionRow(
        Guid Id, Guid TrackId, string Kind, Guid? PersonId, string? PersonName, Guid? OtherTrackId, DateOnly? OtherDay, int? OtherOrdinal, float Similarity);

    /// <summary>
    /// Records the effective <c>tracks.enabled</c> for the readers' SQL (<see cref="SpeakerLabel.Joins"/>); true when it changed.
    /// </summary>
    public async Task<bool> ApplyEnabledAsync(bool enabled, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into tracks_state (id, enabled, updated_at) values (1, @enabled, @now)
            on conflict (id) do update set enabled = excluded.enabled, updated_at = excluded.updated_at
            where tracks_state.enabled is distinct from excluded.enabled
            """,
            new { enabled, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// The local days before <paramref name="before"/> (UTC) with a line that has a fingerprint of <paramref name="model"/> and no
    /// track, oldest first. A media line, and a line with no fingerprint, never counts, so a built day is not due again.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> DaysWithoutTracksAsync(string model, string zone, DateTimeOffset before, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var days = await connection.QueryAsync<DateOnly>(new CommandDefinition(
            """
            select distinct (s.started_at at time zone @zone)::date as Day
            from segments s
            where s.track_id is null and s.started_at < @before and s.speech_kind is distinct from 'media'
              and (exists (select 1 from track_prints p where p.segment_id = s.id and p.model = @model)
                   or exists (select 1 from segment_fingerprints f where f.segment_id = s.id and f.model = @model))
            order by 1
            limit @limit
            """,
            new { model, zone, before, limit }, cancellationToken: ct));
        return days.ToList();
    }

    /// <summary>
    /// The day's lines that may be clustered: not media, with a fingerprint of <paramref name="model"/> (from <c>track_prints</c>,
    /// else the one <c>segment_fingerprints</c> holds), in time order. <see cref="TrackLine.Label"/> keys the provider's label by the
    /// request that made it: the paid pass, else the batch.
    /// </summary>
    public async Task<IReadOnlyList<TrackLine>> DayLinesAsync(string model, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<LineRow>(new CommandDefinition(
            $"""
            select s.id as SegmentId,
                   case when nullif(btrim(s.speaker), '') is not null
                        then coalesce('p' || s.pass_id::text, 'b' || s.batch_id::text) || ':' || btrim(s.speaker) end as Label,
                   s.started_at as StartedAt, s.ended_at as EndedAt, {SpeakerLabel.IsUser} as IsUser, s.track_id as TrackId,
                   p.fingerprint as Fingerprint
            from segments s
            join lateral (
                select tp.fingerprint, tp.idx from track_prints tp where tp.segment_id = s.id and tp.model = @model
                union all
                select f.fingerprint, 0 from segment_fingerprints f
                where f.segment_id = s.id and f.model = @model and not exists (select 1 from track_prints x where x.segment_id = s.id)
            ) p on true
            where s.started_at >= @from and s.started_at < @to and s.speech_kind is distinct from 'media'
            order by s.started_at, s.id, p.idx
            """,
            new { model, from, to }, cancellationToken: ct));
        return rows
            .GroupBy(r => r.SegmentId)
            .Select(g =>
            {
                var first = g.First();
                return new TrackLine(
                    first.SegmentId, first.Label, first.StartedAt, first.EndedAt, first.IsUser, first.TrackId,
                    [.. g.Select(r => VoiceStore.Decode(r.Fingerprint))]);
            })
            .ToList();
    }

    /// <summary>The manual answers whose two lines are both in <paramref name="segmentIds"/>.</summary>
    public async Task<IReadOnlyList<TrackAnswer>> AnswersAsync(IReadOnlyCollection<long> segmentIds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var ids = segmentIds.ToArray();
        return (await connection.QueryAsync<(long SegmentId, long OtherSegmentId, string Answer)>(new CommandDefinition(
            "select segment_id, other_segment_id, answer from track_answers where segment_id = any(@ids) and other_segment_id = any(@ids)",
            new { ids }, cancellationToken: ct)))
            .Select(a => new TrackAnswer(a.SegmentId, a.OtherSegmentId, a.Answer == AnswerSame))
            .ToList();
    }

    /// <summary>The owner's answer about two lines; a new answer replaces the old one. False when a line does not exist or both are one.</summary>
    public async Task<bool> AnswerAsync(long segmentId, long otherSegmentId, bool same, DateTimeOffset now, CancellationToken ct)
    {
        if (segmentId == otherSegmentId)
        {
            return false;
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            return await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into track_answers (segment_id, other_segment_id, answer, answered_at)
                select least(a.id, b.id), greatest(a.id, b.id), @answer, @now
                from segments a, segments b where a.id = @segmentId and b.id = @otherSegmentId
                on conflict (segment_id, other_segment_id) do update set answer = excluded.answer, answered_at = excluded.answered_at
                """,
                new { segmentId, otherSegmentId, answer = same ? AnswerSame : AnswerDifferent, now }, cancellationToken: ct)) == 1;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return false;
        }
    }

    /// <summary>
    /// A paid pass replaced lines: moves the answers about them to the lines that replace them, inside the caller's transaction,
    /// before the old lines go. An answer whose two lines become one is dropped; a newer answer on the same pair wins.
    /// </summary>
    public static async Task RepointAnswersAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long[] oldIds, long[] newIds, CancellationToken ct)
    {
        if (oldIds.Length == 0)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            with moved as (
                select coalesce(m1.new_id, a.segment_id) as x, coalesce(m2.new_id, a.other_segment_id) as y, a.answer, a.answered_at
                from track_answers a
                left join unnest(@oldIds, @newIds) as m1 (old_id, new_id) on m1.old_id = a.segment_id
                left join unnest(@oldIds, @newIds) as m2 (old_id, new_id) on m2.old_id = a.other_segment_id
                where a.segment_id = any(@oldIds) or a.other_segment_id = any(@oldIds))
            insert into track_answers (segment_id, other_segment_id, answer, answered_at)
            select least(x, y), greatest(x, y), answer, answered_at from moved where x <> y
            on conflict (segment_id, other_segment_id) do update set answer = excluded.answer, answered_at = excluded.answered_at
            where track_answers.answered_at < excluded.answered_at
            """,
            new { oldIds, newIds }, transaction, cancellationToken: ct));
    }

    /// <summary>The day's tracks, for a rebuild to keep what it can.</summary>
    public async Task<IReadOnlyList<OldTrack>> DayTracksAsync(DateOnly day, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<OldTrack>(new CommandDefinition(
            "select id as Id, person_id as PersonId, dismissed as Dismissed, skipped_until as SkippedUntil from speaker_tracks where day = @day",
            new { day }, cancellationToken: ct))).ToList();
    }

    /// <summary>The centroids of <paramref name="model"/>'s tracks before <paramref name="day"/>, newest first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<TrackCentroid>> EarlierTracksAsync(string model, DateOnly day, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<CentroidRow>(new CommandDefinition(
            """
            select id as Id, day as Day, centroid as Centroid, seconds::float8 as Seconds, person_id as PersonId
            from speaker_tracks where model = @model and day < @day
            order by day desc, ordinal
            limit @limit
            """,
            new { model, day, limit }, cancellationToken: ct));
        return rows.Select(r => new TrackCentroid(r.Id, r.Day, VoiceStore.Decode(r.Centroid), r.Seconds, r.PersonId)).ToList();
    }

    /// <summary>
    /// Replaces the day's tracks in one transaction: the lines of <paramref name="tracks"/> get their tracks (a track in it keeps its row
    /// when its id is an old one's), an old track left with no line goes (its pending suggestions with it), the day's tracks are numbered
    /// by their first line again, and the day's pending suggestions are replaced by <paramref name="suggestions"/>. A decided suggestion
    /// stays. A line the plan did not take (its fingerprint is gone) stays in its old track while that track lives.
    /// </summary>
    public async Task ReplaceDayAsync(
        DateOnly day, string model, IReadOnlyList<NewTrack> tracks, IReadOnlyList<NewTrackSuggestion> suggestions, DateTimeOffset now, CancellationToken ct)
    {
        var date = day;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from track_suggestions where status = 'pending' and track_id in (select id from speaker_tracks where day = @date)",
            new { date }, transaction, cancellationToken: ct));

        foreach (var track in tracks)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into speaker_tracks (id, day, model, centroid, seconds, segments, ordinal, person_id, dismissed, skipped_until, built_at)
                values (@Id, @date, @model, @centroid, @Seconds, @segments, 0, @PersonId, @Dismissed, @SkippedUntil, @now)
                on conflict (id) do update set
                    model = excluded.model, centroid = excluded.centroid, seconds = excluded.seconds, segments = excluded.segments,
                    built_at = excluded.built_at
                """,
                new
                {
                    track.Id, date, model, centroid = VoiceStore.Encode(track.Centroid), track.Seconds, segments = track.SegmentIds.Count,
                    track.PersonId, track.Dismissed, track.SkippedUntil, now,
                },
                transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "update segments set track_id = @id where id = any(@ids)",
                new { id = track.Id, ids = track.SegmentIds.ToArray() }, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from speaker_tracks t
            where t.day = @date and not exists (select 1 from segments s where s.track_id = t.id)
            """,
            new { date }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update speaker_tracks t set ordinal = r.n
            from (select x.id, row_number() over (order by (select min(s.started_at) from segments s where s.track_id = x.id), x.id) as n
                  from speaker_tracks x where x.day = @date) r
            where t.id = r.id
            """,
            new { date }, transaction, cancellationToken: ct));

        foreach (var suggestion in suggestions.Where(s => tracks.Any(t => t.Id == s.TrackId)))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into track_suggestions (id, track_id, kind, person_id, other_track_id, similarity, created_at)
                values (@id, @TrackId, @kind, @PersonId, @OtherTrackId, @Similarity, @now)
                on conflict do nothing
                """,
                new
                {
                    id = Guid.NewGuid(), suggestion.TrackId, kind = suggestion.PersonId is null ? "track" : "person", suggestion.PersonId,
                    suggestion.OtherTrackId, suggestion.Similarity, now,
                },
                transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>The tracks of a local day (all days when null), newest day first and in order within a day, with their pending suggestions.</summary>
    public async Task<IReadOnlyList<TrackRow>> ListAsync(DateOnly? day, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var date = day;
        var tracks = (await connection.QueryAsync<ListRow>(new CommandDefinition(
            """
            select t.id as Id, t.day as Day, t.ordinal as Ordinal, t.seconds::float8 as Seconds, t.segments as Segments,
                   t.person_id as PersonId, p.name as PersonName, t.dismissed as Dismissed
            from speaker_tracks t left join people p on p.id = t.person_id
            where cast(@date as date) is null or t.day = @date
            order by t.day desc, t.ordinal
            """,
            new { date }, cancellationToken: ct))).ToList();
        var suggestions = (await connection.QueryAsync<SuggestionRow>(new CommandDefinition(
            """
            select g.id as Id, g.track_id as TrackId, g.kind as Kind, g.person_id as PersonId, p.name as PersonName,
                   g.other_track_id as OtherTrackId, o.day as OtherDay, o.ordinal as OtherOrdinal, g.similarity as Similarity
            from track_suggestions g
            join speaker_tracks t on t.id = g.track_id
            left join people p on p.id = g.person_id
            left join speaker_tracks o on o.id = g.other_track_id
            where g.status = 'pending' and (cast(@date as date) is null or t.day = @date)
            order by g.similarity desc
            """,
            new { date }, cancellationToken: ct))).ToLookup(s => s.TrackId);
        return tracks.Select(t => new TrackRow(
            t.Id, t.Day, t.Ordinal, t.Seconds, t.Segments, t.PersonId, t.PersonName, t.Dismissed,
            [.. suggestions[t.Id].Select(s => new TrackSuggestionRow(
                s.Id, s.Kind, s.PersonId, s.PersonName, s.OtherTrackId, s.OtherDay, s.OtherOrdinal, s.Similarity))])).ToList();
    }

    /// <summary>
    /// Accepts or rejects a pending suggestion. Accepting a person sets it on the track; accepting an earlier track gives this track
    /// that track's person, and is <see cref="TrackDecision.NoPerson"/> while that track has none.
    /// </summary>
    public async Task<TrackDecision> DecideAsync(Guid id, bool accept, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var suggestion = await connection.QuerySingleOrDefaultAsync<(Guid TrackId, Guid? PersonId, Guid? OtherPerson)?>(new CommandDefinition(
            """
            select g.track_id, g.person_id, o.person_id
            from track_suggestions g left join speaker_tracks o on o.id = g.other_track_id
            where g.id = @id and g.status = 'pending' for update of g
            """,
            new { id }, transaction, cancellationToken: ct));
        if (suggestion is null)
        {
            return TrackDecision.NotFound;
        }

        if (accept)
        {
            var person = suggestion.Value.PersonId ?? suggestion.Value.OtherPerson;
            if (person is null)
            {
                return TrackDecision.NoPerson;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "update speaker_tracks set person_id = @person where id = @track",
                new { person, track = suggestion.Value.TrackId }, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update track_suggestions set status = @status, decided_at = @now where id = @id",
            new { id, status = accept ? "accepted" : "rejected", now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return TrackDecision.Ok;
    }
}
