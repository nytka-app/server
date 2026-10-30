using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A golden day: <paramref name="Date"/> is <c>yyyy-MM-dd</c>, <paramref name="From"/> to <paramref name="To"/> its UTC bounds.</summary>
public sealed record GoldenDay(string Date, DateTime From, DateTime To);

/// <summary>
/// One answer to a golden item. <paramref name="PersonId"/> goes with <c>person</c>, <paramref name="SameAsSegmentId"/> with <c>same-as</c>.
/// </summary>
public sealed record GoldenAnswer(Guid ClientId, string Answer, Guid? PersonId, long? SameAsSegmentId);

/// <summary>The golden item <see cref="GoldenStore.ItemAsync"/> found: its kind and its lines, the earlier one first.</summary>
public sealed record GoldenItem(string Task, long[] SegmentIds);

/// <summary>What <see cref="GoldenStore.AnswerAsync"/> did: <see cref="Found"/> false for an unknown item.</summary>
public sealed record GoldenAnswered(bool Found);

/// <summary>How far one golden day is: lines with text and the speaker answers among them, boundary items and their answers.</summary>
public sealed record GoldenProgress(string Date, int Lines, int Answered, int Boundaries, int BoundariesAnswered);

/// <summary>An answered golden speaker line, as the owner said it.</summary>
public sealed record GoldenSpeakerLabel(long SegmentId, string Answer, Guid? PersonId, long? OtherSegmentId);

/// <summary>A line as the system shows it: its class (<see cref="QualityStore.LineClass"/>), person and track (a voice group, else the batch's raw label).</summary>
public sealed record GoldenLine(long Id, DateTime StartedAt, string Class, Guid? PersonId, string Track);

/// <summary>An answered golden boundary and whether the system puts a boundary there (another conversation, or a topic shift).</summary>
public sealed record GoldenBoundaryLabel(string Answer, bool SystemNew);

/// <summary>Everything the golden scores read: the speaker answers, the system's view of the lines they name, and the boundary answers.</summary>
public sealed record GoldenData(
    IReadOnlyList<GoldenSpeakerLabel> Speakers, IReadOnlyDictionary<long, GoldenLine> Lines, IReadOnlyList<GoldenBoundaryLabel> Boundaries);

/// <summary>
/// The golden set (docs/explanation/features.md, Labelling): on the days in <c>eval.goldenDays</c> every line with text is a
/// <c>speaker</c> item and the adjacent lines worth asking about are <c>boundary</c> items. An answer is a row of
/// <c>golden_labels</c> and nothing else: it never touches segments, people or voiceprints.
/// </summary>
public sealed class GoldenStore(NpgsqlDataSource dataSource)
{
    public const string Speaker = "speaker";

    public const string Boundary = "boundary";

    public const string Reason = "golden";

    public static readonly string[] SpeakerAnswers = ["wearer", "person", "same-as", "new-voice", "media", "crowd", "unsure"];

    public static readonly string[] BoundaryAnswers = ["same-situation", "new-situation"];

    /// <summary>
    /// Makes an item of each kind for what the days hold that has none yet, answered or not: a speaker item for every line with text,
    /// ranked by its speech seconds, and a boundary item for every adjacent pair of lines across a conversation split or a topic shift,
    /// and for the widest gap inside each conversation (the split the system did not make that is likeliest), ranked by the pair's
    /// speech seconds. Rank is minus the seconds, so the longest speech comes first.
    /// </summary>
    public async Task EnsureItemsAsync(IReadOnlyList<GoldenDay> days, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        foreach (var day in days)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"""
                insert into labeling_items (task, conversation_id, segment_ids, started_at, ended_at, reason, rank, speaker_key, created_at)
                select '{Speaker}', s.conversation_id, array[s.id], s.started_at, s.ended_at, '{Reason}',
                       (-extract(epoch from s.ended_at - s.started_at))::real, {LabelStore.SpeakerKey}, @now
                from segments s left join segment_fingerprints f on f.segment_id = s.id
                where s.started_at >= @from and s.started_at < @to and btrim(s.text) <> ''
                  and not exists (select 1 from labeling_items i where i.task = '{Speaker}' and i.segment_ids @> array[s.id])
                on conflict (task, segment_ids) where answered_at is null do nothing
                """,
                new { day.From, day.To, now }, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                $"""
                with l as (
                    select s.id, s.conversation_id, s.started_at, s.ended_at,
                           lag(s.id) over w as prev_id, lag(s.conversation_id) over w as prev_conversation,
                           lag(s.started_at) over w as prev_started, lag(s.ended_at) over w as prev_ended
                    from segments s
                    where s.started_at >= @from and s.started_at < @to and btrim(s.text) <> ''
                    window w as (order by s.started_at, s.id)
                ), p as (
                    select l.*, l.prev_conversation <> l.conversation_id as split, l.id = any(c.topic_boundaries) as topic,
                           row_number() over (partition by l.conversation_id, l.prev_conversation = l.conversation_id
                                              order by l.started_at - l.prev_ended desc, l.id) as widest
                    from l join conversations c on c.id = l.conversation_id
                    where l.prev_id is not null
                )
                insert into labeling_items (task, conversation_id, segment_ids, started_at, ended_at, reason, rank, created_at)
                select '{Boundary}', prev_conversation, array[prev_id, id], prev_started, ended_at, '{Reason}',
                       (-(extract(epoch from ended_at - started_at) + extract(epoch from prev_ended - prev_started)))::real, @now
                from p
                where (split or topic or widest = 1)
                  and not exists (select 1 from labeling_items i where i.task = '{Boundary}' and i.segment_ids = array[p.prev_id, p.id])
                on conflict (task, segment_ids) where answered_at is null do nothing
                """,
                new { day.From, day.To, now }, cancellationToken: ct));
        }
    }

    /// <summary>
    /// The ids of up to <paramref name="take"/> open items of <paramref name="task"/> that no lease holds and that fall on a golden
    /// day, the longest speech first.
    /// </summary>
    public async Task<long[]> OpenAsync(string task, IReadOnlyList<GoldenDay> days, int take, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<long>(new CommandDefinition(
            """
            select i.id from labeling_items i
            where i.task = @task and i.answered_at is null and (i.leased_until is null or i.leased_until <= @now)
              and exists (select 1 from unnest(@froms, @tos) as d(f, t) where i.started_at >= d.f and i.started_at < d.t)
            order by i.rank, i.started_at, i.id
            limit @take
            """,
            new { task, take, now, froms = days.Select(d => d.From).ToArray(), tos = days.Select(d => d.To).ToArray() },
            cancellationToken: ct))).ToArray();
    }

    /// <summary>Per golden task: the items waiting on the days and the answers given for them.</summary>
    public async Task<IReadOnlyList<(string Task, int Open, int Labelled)>> CountsAsync(IReadOnlyList<GoldenDay> days, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var (froms, tos) = (days.Select(d => d.From).ToArray(), days.Select(d => d.To).ToArray());
        return (await connection.QueryAsync<(string, int, int)>(new CommandDefinition(
            """
            select t.task,
                   (select count(*)::int from labeling_items i where i.task = t.task and i.answered_at is null
                      and exists (select 1 from unnest(@froms, @tos) as d(f, t) where i.started_at >= d.f and i.started_at < d.t)),
                   (select count(*)::int from golden_labels g join segments s on s.id = g.segment_id
                    where g.kind = t.task and exists (select 1 from unnest(@froms, @tos) as d(f, t) where s.started_at >= d.f and s.started_at < d.t))
            from unnest(@tasks) as t(task)
            """,
            new { tasks = LabelingTasks.Golden, froms, tos }, cancellationToken: ct))).AsList();
    }

    /// <summary>The kind and lines of a golden item, null for an unknown one or one of another task.</summary>
    public async Task<GoldenItem?> ItemAsync(long itemId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            "select task as Task, segment_ids as SegmentIds from labeling_items where id = @itemId and task in ('speaker', 'boundary')",
            new { itemId }, cancellationToken: ct));
        return row is null ? null : new GoldenItem(row.Task, row.SegmentIds);
    }

    /// <summary>Whether the segment exists and starts on one of the golden days.</summary>
    public async Task<bool> IsGoldenSegmentAsync(long segmentId, IReadOnlyList<GoldenDay> days, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (select 1 from segments s, unnest(@froms, @tos) as d(f, t)
                           where s.id = @segmentId and s.started_at >= d.f and s.started_at < d.t)
            """,
            new { segmentId, froms = days.Select(d => d.From).ToArray(), tos = days.Select(d => d.To).ToArray() },
            cancellationToken: ct));
    }

    /// <summary>
    /// Stores the answer as the label of the item's line (a boundary's later one) and marks the item answered, in one transaction. A
    /// new answer replaces the old one; a client id seen before changes nothing. Segments, people and voiceprints are not touched.
    /// </summary>
    public async Task<GoldenAnswered> AnswerAsync(long itemId, GoldenAnswer answer, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var found = await connection.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            "select task as Task, segment_ids as SegmentIds from labeling_items where id = @itemId and task in ('speaker', 'boundary') for update",
            new { itemId }, transaction, cancellationToken: ct));
        if (found is null)
        {
            return new GoldenAnswered(false);
        }

        if (await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from golden_labels where client_id = @ClientId)", answer, transaction, cancellationToken: ct)))
        {
            return new GoldenAnswered(true);
        }

        var boundary = found.Task == Boundary;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into golden_labels (kind, segment_id, answer, person_id, other_segment_id, item_id, client_id, answered_at)
            values (@kind, @segmentId, @Answer, @PersonId, @other, @itemId, @ClientId, @now)
            on conflict (kind, segment_id) do update
            set answer = excluded.answer, person_id = excluded.person_id, other_segment_id = excluded.other_segment_id,
                item_id = excluded.item_id, client_id = excluded.client_id, answered_at = excluded.answered_at
            """,
            new
            {
                kind = found.Task, segmentId = found.SegmentIds[^1], answer.Answer, answer.PersonId, answer.ClientId, itemId, now,
                other = boundary ? found.SegmentIds[0] : answer.SameAsSegmentId,
            },
            transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update labeling_items set answered_at = @now, lease_task = null, lease_session = null, lease_order = null, leased_until = null
            where id = @itemId
            """,
            new { itemId, now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return new GoldenAnswered(true);
    }

    /// <summary>Per day: the lines with text, the speaker answers among them, the boundary items and their answers.</summary>
    public async Task<IReadOnlyList<GoldenProgress>> ProgressAsync(IReadOnlyList<GoldenDay> days, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var progress = new List<GoldenProgress>();
        foreach (var day in days)
        {
            progress.Add(await connection.QuerySingleAsync<GoldenProgress>(new CommandDefinition(
                """
                select @date as Date,
                       (select count(*)::int from segments s where s.started_at >= @from and s.started_at < @to and btrim(s.text) <> '') as Lines,
                       (select count(*)::int from golden_labels g join segments s on s.id = g.segment_id
                        where g.kind = 'speaker' and s.started_at >= @from and s.started_at < @to and btrim(s.text) <> '') as Answered,
                       (select count(*)::int from labeling_items i
                        where i.task = 'boundary' and i.started_at >= @from and i.started_at < @to) as Boundaries,
                       (select count(*)::int from golden_labels g join segments s on s.id = g.segment_id
                        where g.kind = 'boundary' and s.started_at >= @from and s.started_at < @to) as BoundariesAnswered
                """,
                new { date = day.Date, day.From, day.To }, cancellationToken: ct)));
        }

        return progress;
    }

    /// <summary>
    /// The first and second line of a boundary item as they stand, earlier one first, for its clip; null for another kind or an
    /// unknown item.
    /// </summary>
    public async Task<IReadOnlyList<(Guid ConversationId, DateTime StartedAt, DateTime EndedAt)>?> BoundaryLinesAsync(long itemId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(Guid, DateTime, DateTime)>(new CommandDefinition(
            """
            select s.conversation_id, s.started_at, s.ended_at
            from labeling_items i join segments s on s.id = any(i.segment_ids)
            where i.id = @itemId and i.task = 'boundary'
            order by s.started_at, s.id
            """,
            new { itemId }, cancellationToken: ct))).ToList();
        return rows.Count == 2 ? rows : null;
    }

    /// <summary>A class: Dapper cannot match a constructor to a <c>bigint[]</c> column.</summary>
    private sealed class ItemRow
    {
        public string Task { get; set; } = "";

        public long[] SegmentIds { get; set; } = [];
    }

    private sealed record SpeakerRow(long SegmentId, string Answer, Guid? PersonId, long? OtherSegmentId);

    private sealed record BoundaryRow(string Answer, bool SystemNew);

    private sealed record LineRow(long Id, DateTime StartedAt, string Class, Guid? PersonId, string Track);

    /// <summary>
    /// Every golden answer with the system's view of the lines they name (and the lines a <c>same-as</c> points at), as the quality job
    /// scores them. A line a paid pass replaced is not in <c>Lines</c> and does not count.
    /// </summary>
    public async Task<GoldenData> DataAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var speakers = (await connection.QueryAsync<SpeakerRow>(new CommandDefinition(
            "select segment_id as SegmentId, answer as Answer, person_id as PersonId, other_segment_id as OtherSegmentId from golden_labels where kind = 'speaker'",
            cancellationToken: ct))).Select(r => new GoldenSpeakerLabel(r.SegmentId, r.Answer, r.PersonId, r.OtherSegmentId)).ToList();
        var ids = speakers.SelectMany(s => s.OtherSegmentId is { } other ? new[] { s.SegmentId, other } : [s.SegmentId]).Distinct().ToArray();
        var lines = (await connection.QueryAsync<LineRow>(new CommandDefinition(
            $"""
            with l as (
                select s.id, s.started_at, coalesce({SpeakerLabel.IsUser}, false) as wearer, {SpeakerLabel.PersonId} as person_id, s.speaker,
                       coalesce({SpeechKinds.IsMedia}, false) as media,
                       coalesce('g:' || f.group_id::text,
                                case when coalesce(nullif(btrim(s.speaker_id), ''), nullif(btrim(s.speaker), '')) is not null
                                     then 'b:' || coalesce(s.batch_id::text, '') || ':' || coalesce(nullif(btrim(s.speaker_id), ''), btrim(s.speaker)) end,
                                'none') as track
                from segments s left join segment_fingerprints f on f.segment_id = s.id {SpeakerLabel.Joins}
                where s.id = any(@ids)
            )
            select id as Id, started_at as StartedAt, {QualityStore.LineClass} as Class, person_id as PersonId, track as Track from l
            """,
            new { ids }, cancellationToken: ct))).ToDictionary(r => r.Id, r => new GoldenLine(r.Id, r.StartedAt, r.Class, r.PersonId, r.Track));
        var boundaries = (await connection.QueryAsync<BoundaryRow>(new CommandDefinition(
            """
            select g.answer as Answer, a.conversation_id <> b.conversation_id or b.id = any(c.topic_boundaries) as SystemNew
            from golden_labels g
            join segments b on b.id = g.segment_id join segments a on a.id = g.other_segment_id
            join conversations c on c.id = b.conversation_id
            where g.kind = 'boundary'
            """,
            cancellationToken: ct))).Select(r => new GoldenBoundaryLabel(r.Answer, r.SystemNew)).ToList();
        return new GoldenData(speakers, lines, boundaries);
    }
}
