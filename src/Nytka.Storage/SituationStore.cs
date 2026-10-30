using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A closed conversation of a day to cut.</summary>
public sealed record SituationConversation(Guid Id, DateTime StartedAt, DateTime EndedAt);

/// <summary>A situation as the reader sees it: <see cref="People"/> are named from its lines, as the scenes' are.</summary>
public sealed record SituationView(
    Guid Id, Guid ConversationId, DateTime StartedAt, DateTime EndedAt, string StartsWith, string? Place, string Activity, double MediaShare,
    bool Noise, string? Title, string? Summary, int Version, IReadOnlyList<ScenePerson> People);

/// <summary>A situation whose title and summary are due: its lines, to be read through its conversation.</summary>
public sealed record SituationToEnrich(Guid Id, Guid ConversationId, string LinesHash, DateTime StartedAt);

/// <summary>What a save of a day did: situations new, changed (a version more), unchanged and removed.</summary>
public sealed record SituationSave(int Created, int Changed, int Unchanged, int Removed);

/// <summary>A stored situation, for queuing the links within it: <paramref name="Last"/> is true for the last of its conversation.</summary>
public sealed record StoredSituation(Guid Id, Guid ConversationId, DateTime StartedAt, DateTime EndedAt, bool Last);

/// <summary>
/// Situations (<c>situations</c>, <c>situation_segments</c> and <c>situation_days</c>, docs/explanation/features.md#situations): a
/// day's cut, stored in one transaction so a reader never sees half a day, and the record of their titles and summaries. A rebuild of
/// unchanged input changes no row.
/// </summary>
public sealed class SituationStore(NpgsqlDataSource dataSource)
{
    public const string StageCut = "cut";
    public const string StageFinal = "final";

    public const string AiDone = "done";
    public const string AiSkipped = "skipped";
    public const string AiFailed = "failed";

    private sealed record ExistingRow(Guid Id, string Signature);

    private sealed record LineRow(Guid ConversationId, long SegmentId, DateTime StartedAt, DateTime EndedAt, Guid? TrackId, bool IsUser, bool IsMedia, int Words);

    private sealed record ViewRow(
        Guid Id, Guid ConversationId, DateTime StartedAt, DateTime EndedAt, string StartsWith, string? Place, string Activity, double MediaShare,
        bool Noise, string? Title, string? Summary, int Version);

    private sealed record PersonRow(Guid SituationId, Guid? PersonId, string Name);

    /// <summary>A situation's id: a hash of its conversation and start, so a rebuild finds the same row again.</summary>
    public static Guid IdOf(Guid conversation, DateTime startedAt) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"situation:{conversation}:{startedAt.Ticks}")))[..16]);

    /// <summary>A hash of the lines of a situation: it changes when a line comes or goes, not when the tracks move.</summary>
    public static string LinesHashOf(IReadOnlyList<long> segmentIds) =>
        Hex(string.Join(',', segmentIds.Select(id => id.ToString(CultureInfo.InvariantCulture))));

    private static string SignatureOf(SituationSpan span, string linesHash) =>
        Hex(FormattableString.Invariant(
            $"{span.EndedAt.Ticks}|{span.StartsWith}|{span.Noise}|{span.MediaShare:R}|{string.Join(',', span.TrackIds)}|{linesHash}"));

    private static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The closed conversations that started in [<paramref name="from"/>, <paramref name="to"/>), oldest first.</summary>
    public async Task<IReadOnlyList<SituationConversation>> ConversationsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<SituationConversation>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, coalesce(ended_at, started_at) as EndedAt
            from conversations where status = 'closed' and started_at >= @from and started_at < @to
            order by started_at, id
            """,
            new { from, to }, cancellationToken: ct))).ToList();
    }

    /// <summary>The lines of the conversations, in time order within each; the track is what the line is in now, or null.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<SituationLine>>> LinesAsync(Guid[] conversations, bool tracks, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<LineRow>(new CommandDefinition(
            $"""
            select s.conversation_id as ConversationId, s.id as SegmentId, s.started_at as StartedAt, s.ended_at as EndedAt,
                   case when @tracks then s.track_id end as TrackId, coalesce({SpeakerLabel.IsUser}, false) as IsUser,
                   coalesce(s.speech_kind = 'media', false) as IsMedia,
                   coalesce(cardinality(regexp_split_to_array(nullif(btrim(s.text), ''), '\s+')), 0) as Words
            from segments s
            where s.conversation_id = any(@conversations)
            order by s.conversation_id, s.started_at, s.id
            """,
            new { conversations, tracks }, cancellationToken: ct));
        return rows.GroupBy(r => r.ConversationId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<SituationLine>)[.. g.Select(r => new SituationLine(r.SegmentId, r.StartedAt, r.EndedAt, r.TrackId, r.IsUser, r.IsMedia, r.Words))]);
    }

    /// <summary>
    /// Replaces the day's situations of <paramref name="conversations"/> by <paramref name="spans"/> (per conversation) and records the
    /// day's stage, in one transaction. A situation whose content is the same is not touched; one that changed keeps its summary and
    /// gets the next version; one that no longer exists goes with its lines.
    /// </summary>
    public async Task<SituationSave> SaveDayAsync(
        DateOnly day, string stage, IReadOnlyDictionary<Guid, IReadOnlyList<SituationSpan>> spans, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var existing = (await connection.QueryAsync<ExistingRow>(new CommandDefinition(
            "select id as Id, signature as Signature from situations where day = @day for update",
            new { day }, transaction, cancellationToken: ct))).ToDictionary(r => r.Id, r => r.Signature);

        var wanted = spans
            .OrderBy(s => s.Key)
            .SelectMany(s => s.Value.Select(span =>
            {
                var hash = LinesHashOf(span.SegmentIds);
                return (Conversation: s.Key, Span: span, Id: IdOf(s.Key, span.StartedAt), LinesHash: hash, Signature: SignatureOf(span, hash));
            }))
            .ToList();
        var keep = wanted.Select(w => w.Id).ToHashSet();
        var removed = existing.Keys.Where(id => !keep.Contains(id)).ToArray();
        if (removed.Length > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from situations where id = any(@removed)", new { removed }, transaction, cancellationToken: ct));
        }

        int created = 0, changed = 0, unchanged = 0;
        foreach (var w in wanted)
        {
            if (existing.TryGetValue(w.Id, out var signature))
            {
                if (signature == w.Signature)
                {
                    unchanged++;
                    continue;
                }

                changed++;
            }
            else
            {
                created++;
            }

            await connection.ExecuteAsync(new CommandDefinition("delete from situation_segments where situation_id = @id", new { w.Id }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into situations (id, day, conversation_id, started_at, ended_at, starts_with, track_ids, activity, media_share, noise, version, lines_hash, signature, built_at)
                values (@Id, @day, @Conversation, @StartedAt, @EndedAt, @StartsWith, @trackIds, @Activity, @MediaShare, @Noise, 1, @LinesHash, @Signature, @now)
                on conflict (id) do update set
                    ended_at = excluded.ended_at, starts_with = excluded.starts_with, track_ids = excluded.track_ids, activity = excluded.activity,
                    media_share = excluded.media_share, noise = excluded.noise, version = situations.version + 1, lines_hash = excluded.lines_hash,
                    signature = excluded.signature, built_at = excluded.built_at
                """,
                new
                {
                    w.Id, day, w.Conversation, w.Span.StartedAt, w.Span.EndedAt, w.Span.StartsWith, trackIds = w.Span.TrackIds.ToArray(), w.Span.Activity,
                    MediaShare = (float)w.Span.MediaShare, w.Span.Noise, w.LinesHash, w.Signature, now,
                },
                transaction, cancellationToken: ct));
        }

        // Inserted after every delete above, so a line that moved to another situation is not held by the old one.
        foreach (var w in wanted.Where(w => !existing.TryGetValue(w.Id, out var s) || s != w.Signature))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "insert into situation_segments (situation_id, segment_id) select @Id, unnest(@segments) on conflict (segment_id) do update set situation_id = excluded.situation_id",
                new { w.Id, segments = w.Span.SegmentIds.ToArray() }, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into situation_days (day, stage, built_at) values (@day, @stage, @now)
            on conflict (day) do update set stage = excluded.stage, built_at = excluded.built_at
            """,
            new { day, stage, now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return new SituationSave(created, changed, unchanged, removed.Length);
    }

    /// <summary>Sets the stage of a built day; a day never built is left alone.</summary>
    public async Task SetStageAsync(DateOnly day, string stage, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "update situation_days set stage = @stage where day = @day", new { day, stage }, cancellationToken: ct));
    }

    /// <summary>The situations of a local day, oldest first, with whether each is the last of its conversation.</summary>
    public async Task<IReadOnlyList<StoredSituation>> DayAsync(DateOnly day, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<StoredSituation>(new CommandDefinition(
            """
            select s.id as Id, s.conversation_id as ConversationId, s.started_at as StartedAt, s.ended_at as EndedAt,
                   s.started_at = max(s.started_at) over (partition by s.conversation_id) as Last
            from situations s where s.day = @day
            order by s.started_at, s.id
            """,
            new { day }, cancellationToken: ct))).ToList();
    }

    /// <summary>The stage of a built day, or null when it was never built.</summary>
    public async Task<string?> StageAsync(DateOnly day, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select stage from situation_days where day = @day", new { day }, cancellationToken: ct));
    }

    /// <summary>
    /// The local days before <paramref name="before"/> (UTC) that are due for a build, oldest first: every conversation of the day is closed
    /// and none waits for its summary, and the day was never built or a conversation was summarized after the last build.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> DaysDueAsync(string zone, DateTimeOffset before, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<DateOnly>(new CommandDefinition(
            """
            select d.day
            from (
                select (c.started_at at time zone @zone)::date as day, max(c.ai_updated_at) as changed,
                       bool_or(c.status <> 'closed') as open, bool_or(c.ai_status = 'pending') as pending
                from conversations c where c.started_at < @before
                group by 1
            ) d
            left join situation_days sd on sd.day = d.day
            where not d.open and not d.pending and (sd.day is null or d.changed > sd.built_at)
            order by d.day
            limit @limit
            """,
            new { zone, before, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// The days left at the stage <c>cut</c> with no <c>link-speakers</c> or <c>build-tracks</c> job queued for them: the links within their
    /// situations are all asked, so the day can be cut once more.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> DaysToRecutAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<DateOnly>(new CommandDefinition(
            """
            select sd.day from situation_days sd
            where sd.stage = 'cut'
              and not exists (
                  select 1 from jobs j
                  where j.kind in ('link-speakers', 'build-tracks', 'build-situations') and j.payload ->> 'LocalDate' = to_char(sd.day, 'YYYY-MM-DD'))
            order by sd.day
            limit @limit
            """,
            new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// The situations of a built day whose title and summary are due, oldest first: lines the summary was not made from, and not noise.
    /// A failed one comes back once it is older than <paramref name="retryBefore"/>, up to <paramref name="maxFailures"/> times.
    /// </summary>
    public async Task<IReadOnlyList<SituationToEnrich>> DueForEnrichmentAsync(
        int limit, int maxFailures, DateTimeOffset retryBefore, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<SituationToEnrich>(new CommandDefinition(
            """
            select s.id as Id, s.conversation_id as ConversationId, s.lines_hash as LinesHash, s.started_at as StartedAt
            from situations s join situation_days sd on sd.day = s.day and sd.stage = 'final'
            where not s.noise and s.lines_hash is distinct from s.ai_lines_hash
              and (s.ai_status <> 'failed' or (s.ai_failures < @maxFailures and s.ai_updated_at < @retryBefore))
            order by s.started_at, s.id
            limit @limit
            """,
            new { limit, maxFailures, retryBefore }, cancellationToken: ct))).ToList();
    }

    /// <summary>The situation's conversation and the lines it holds, or null when it is gone.</summary>
    public async Task<(Guid ConversationId, string LinesHash, IReadOnlyList<long> Segments)?> LinesOfAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<(Guid ConversationId, string LinesHash)?>(new CommandDefinition(
            "select conversation_id, lines_hash from situations where id = @id", new { id }, cancellationToken: ct));
        if (row is not { } found)
        {
            return null;
        }

        var segments = await connection.QueryAsync<long>(new CommandDefinition(
            "select segment_id from situation_segments where situation_id = @id order by segment_id", new { id }, cancellationToken: ct));
        return (found.ConversationId, found.LinesHash, segments.ToList());
    }

    /// <summary>
    /// Stores a title and a summary (null for a situation too short to summarize) as made from the lines <paramref name="linesHash"/>.
    /// False when the situation is gone or its lines changed since: the next run reads them again.
    /// </summary>
    public async Task<bool> StoreEnrichmentAsync(
        Guid id, string linesHash, string status, string? title, string? summary, string? message, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            update situations
            set title = @title, summary = @summary, ai_status = @status, ai_message = @message, ai_lines_hash = @linesHash,
                ai_failures = 0, ai_updated_at = @now
            where id = @id and lines_hash = @linesHash
            """,
            new { id, linesHash, status, title, summary, message, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>Records a round of failed attempts.</summary>
    public async Task FailEnrichmentAsync(Guid id, string message, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "update situations set ai_status = 'failed', ai_message = @message, ai_failures = ai_failures + 1, ai_updated_at = @now where id = @id",
            new { id, message, now }, cancellationToken: ct));
    }

    /// <summary>The situations of the conversations, oldest first, with the people who spoke in each (the wearer left out).</summary>
    public async Task<ILookup<Guid, SituationView>> ForConversationsAsync(Guid[] conversations, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<ViewRow>(new CommandDefinition(
            """
            select id as Id, conversation_id as ConversationId, started_at as StartedAt, ended_at as EndedAt, starts_with as StartsWith,
                   place as Place, activity as Activity, media_share::float8 as MediaShare, noise as Noise, title as Title, summary as Summary,
                   version as Version
            from situations where conversation_id = any(@conversations)
            order by started_at, id
            """,
            new { conversations }, cancellationToken: ct))).ToList();
        if (rows.Count == 0)
        {
            return Array.Empty<SituationView>().ToLookup(v => v.ConversationId);
        }

        var ids = rows.Select(r => r.Id).ToArray();
        var people = (await connection.QueryAsync<PersonRow>(new CommandDefinition(
            $"""
            select distinct ss.situation_id as SituationId, {SpeakerLabel.PersonId} as PersonId,
                   coalesce({SpeakerLabel.PersonName}, {SpeakerLabel.Voice}) as Name
            from situation_segments ss join segments s on s.id = ss.segment_id {SpeakerLabel.Joins}
            where ss.situation_id = any(@ids) and not coalesce({SpeakerLabel.IsUser}, false)
              and coalesce({SpeakerLabel.PersonName}, {SpeakerLabel.Voice}) is not null
            """,
            new { ids }, cancellationToken: ct))).ToLookup(p => p.SituationId);
        return rows.ToLookup(
            r => r.ConversationId,
            r => new SituationView(
                r.Id, r.ConversationId, r.StartedAt, r.EndedAt, r.StartsWith, r.Place, r.Activity, r.MediaShare, r.Noise, r.Title, r.Summary,
                r.Version,
                [.. people[r.Id].OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => new ScenePerson(p.PersonId, p.Name))]));
    }
}
