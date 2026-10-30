using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A clip the candidate job proposes, before it is an item.</summary>
public sealed record LabelingCandidate(
    Guid ConversationId, long[] SegmentIds, DateTime StartedAt, DateTime EndedAt, string Reason, float Rank, string? GuessModel,
    string? GuessValue, float? GuessScore, string? SpeakerKey);

/// <summary>An open item as <c>GET /next</c> chooses among them.</summary>
public sealed record OpenLabelingItem(long Id, string Task, string Reason, float Rank, Guid ConversationId, DateTime StartedAt, string? SpeakerKey);

/// <summary>One answer to store. <paramref name="Provenance"/> is <c>app</c> or <c>import:&lt;name&gt;</c>.</summary>
public sealed record NewLabel(
    Guid? ClientId, string[]? Source, string? DeviceType, string? Content, string? Transcript, string[] Flags, string? Language,
    Guid? PersonId, string? Correction, string? Overlap, bool LinesDiffer, int? ListenedMs, string? AnsweredWith, string Provenance);

/// <summary>What <see cref="LabelStore.AnswerAsync"/> did: <see cref="Found"/> false for an unknown item, <see cref="Created"/> false for a client id seen before.</summary>
public sealed record LabelAnswer(bool Found, long? LabelId, long? Superseded, bool Created = false);

/// <summary>One import item, by segment id.</summary>
public sealed record ImportLabel(long[] SegmentIds, string[]? Source, string? Content, string? Transcript, string[] Flags);

/// <summary>A pending <c>me</c> line: the label, the line and its length.</summary>
public sealed record PendingVoiceLine(long LabelId, long SegmentId, int DurationMs);

/// <summary>
/// The labelling app's data (docs/explanation/features.md, Labelling): items the server asks about, labels (append-only, the
/// record of truth), kept clips and the marks labels set on segments. A label's marks change in the label's transaction.
/// </summary>
public sealed class LabelStore(NpgsqlDataSource dataSource)
{
    public const string App = "app";

    public const short TaxonomyVersion = 1;

    /// <summary>How long a <c>GET /next</c> session holds its items.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);

    /// <summary>A label of the app this old may be asked again as a repeat.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromDays(14);

    /// <summary>The most context lines an item carries either side, the nearest ones.</summary>
    public const int ContextLines = 2;

    /// <summary>A segment's lines as the candidate job reads them. A class: Dapper cannot match a constructor to a <c>text[]</c> column.</summary>
    public sealed class CandidateLine
    {
        public long Id { get; set; }

        public Guid ConversationId { get; set; }

        public DateTime StartedAt { get; set; }

        public DateTime EndedAt { get; set; }

        public string Text { get; set; } = "";

        public bool? IsUser { get; set; }

        public bool? ProviderIsUser { get; set; }

        public bool? VoiceIsUser { get; set; }

        public float? VoiceSimilarity { get; set; }

        public string? SpeechGuess { get; set; }

        public float? SpeechScore { get; set; }

        public string[]? SpeechSignals { get; set; }

        public short? SpeechVersion { get; set; }

        public bool Final { get; set; }

        public string? SpeakerKey { get; set; }

        public bool HasAudio { get; set; }

        /// <summary>The tasks that already have an item holding this line.</summary>
        public string[] Tasks { get; set; } = [];
    }

    public sealed record DraftLine(Guid ConversationId, DateTime StartedAt, DateTime EndedAt, string Text);

    /// <summary>Up to <paramref name="count"/> closed conversations that still hold speech audio, at random.</summary>
    public async Task<IReadOnlyList<Guid>> SampleConversationsAsync(int count, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select c.id from conversations c
            where c.status = 'closed' and exists (select 1 from speech_audio a where a.conversation_id = c.id)
            order by random() limit @count
            """,
            new { count }, cancellationToken: ct))).AsList();
    }

    /// <summary>Every line of the conversations with text, in order, with what each task's candidates need.</summary>
    public async Task<IReadOnlyList<CandidateLine>> CandidateLinesAsync(Guid[] conversationIds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<CandidateLine>(new CommandDefinition(
            $"""
            select s.id as Id, s.conversation_id as ConversationId, s.started_at as StartedAt, s.ended_at as EndedAt, s.text as Text,
                   {SpeakerLabel.IsUser} as IsUser, s.is_user as ProviderIsUser, s.voice_is_user as VoiceIsUser,
                   s.voice_similarity as VoiceSimilarity, s.speech_guess as SpeechGuess, s.speech_score as SpeechScore,
                   s.speech_signals as SpeechSignals, s.speech_version as SpeechVersion, s.pass_id is not null as Final,
                   {SpeakerKey} as SpeakerKey,
                   exists (select 1 from speech_audio a where a.conversation_id = s.conversation_id
                           and a.started_at < s.ended_at and a.ended_at > s.started_at) as HasAudio,
                   array(select distinct i.task from labeling_items i where i.segment_ids @> array[s.id]) as Tasks
            from segments s left join segment_fingerprints f on f.segment_id = s.id
            where s.conversation_id = any(@conversationIds) and btrim(s.text) <> ''
            order by s.conversation_id, s.started_at, s.id
            """,
            new { conversationIds }, cancellationToken: ct))).AsList();
    }

    /// <summary>The draft lines a paid pass replaced in the conversations, kept beside the final text.</summary>
    public async Task<IReadOnlyList<DraftLine>> DraftsAsync(Guid[] conversationIds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<DraftLine>(new CommandDefinition(
            """
            select conversation_id as ConversationId, started_at as StartedAt, ended_at as EndedAt, text as Text
            from segment_drafts where conversation_id = any(@conversationIds) order by started_at, id
            """,
            new { conversationIds }, cancellationToken: ct))).AsList();
    }

    /// <summary>
    /// Current app labels of <paramref name="task"/> older than <see cref="RepeatAfter"/> whose lines were never asked again, at
    /// random, as repeat candidates.
    /// </summary>
    public async Task<IReadOnlyList<LabelingCandidate>> RepeatCandidatesAsync(string task, int limit, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ItemRow>(new CommandDefinition(
            """
            select i.id as Id, 'repeat' as Reason, i.conversation_id as ConversationId, i.segment_ids as SegmentIds, i.started_at as StartedAt, i.ended_at as EndedAt,
                   i.guess_model as GuessModel, i.guess_value as GuessValue,
                   i.guess_score as GuessScore, i.speaker_key as SpeakerKey
            from labels l join labeling_items i on i.id = l.item_id
            where l.task = @task and l.provenance = 'app' and l.superseded_by is null and l.created_at < @before and i.reason <> 'repeat'
              and not exists (select 1 from labeling_items r where r.task = @task and r.reason = 'repeat' and r.segment_ids = i.segment_ids)
            order by random() limit @limit
            """,
            new { task, limit, before = now - RepeatAfter }, cancellationToken: ct));
        return [.. rows.Select(r => new LabelingCandidate(
            r.ConversationId, r.SegmentIds, r.StartedAt, r.EndedAt, r.Reason, Random.Shared.NextSingle(), r.GuessModel, r.GuessValue, r.GuessScore,
            r.SpeakerKey))];
    }

    /// <summary>How many items wait per task and reason.</summary>
    public async Task<IReadOnlyList<(string Task, string Reason, int Count)>> OpenCountsAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<(string, string, int)>(new CommandDefinition(
            "select task, reason, count(*)::int from labeling_items where answered_at is null group by task, reason",
            cancellationToken: ct))).AsList();
    }

    /// <summary>Stores the candidates as items of <paramref name="task"/>; one already open for the same lines is left alone. Returns how many it added.</summary>
    public async Task<int> AddItemsAsync(
        string task, IReadOnlyList<(LabelingCandidate Candidate, float[]? Peaks)> candidates, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var added = 0;
        foreach (var (c, peaks) in candidates)
        {
            added += await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into labeling_items (task, conversation_id, segment_ids, started_at, ended_at, reason, rank, guess_model, guess_value,
                                            guess_score, peaks, speaker_key, created_at)
                values (@task, @ConversationId, @SegmentIds, @StartedAt, @EndedAt, @Reason, @Rank, @GuessModel, @GuessValue, @GuessScore,
                        @peaks, @SpeakerKey, @now)
                on conflict (task, segment_ids) where answered_at is null do nothing
                """,
                new
                {
                    task, c.ConversationId, c.SegmentIds, c.StartedAt, c.EndedAt, c.Reason, c.Rank, c.GuessModel, c.GuessValue,
                    c.GuessScore, peaks, c.SpeakerKey, now,
                },
                cancellationToken: ct));
        }

        return added;
    }

    /// <summary>The items an open lease of <paramref name="leaseTask"/> holds that are not answered, in lease order, with its session.</summary>
    public async Task<(Guid Session, DateTime Until, long[] Ids)?> OpenLeaseAsync(string leaseTask, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<(long Id, Guid Session, DateTime Until)>(new CommandDefinition(
            """
            select id, lease_session, leased_until from labeling_items
            where lease_task = @leaseTask and leased_until > @now and answered_at is null
            order by lease_session, lease_order
            """,
            new { leaseTask, now }, cancellationToken: ct))).ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var session = rows[0].Session;
        var own = rows.Where(r => r.Session == session).ToList();
        return (session, own.Max(r => r.Until), [.. own.Select(r => r.Id)]);
    }

    /// <summary>Open items of the tasks no lease holds, the <paramref name="perReason"/> best of each task and reason by rank.</summary>
    public async Task<IReadOnlyList<OpenLabelingItem>> OpenAsync(string[] tasks, int perReason, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<OpenLabelingItem>(new CommandDefinition(
            """
            select Id, Task, Reason, Rank, ConversationId, StartedAt, SpeakerKey from (
                select id as Id, task as Task, reason as Reason, rank as Rank, conversation_id as ConversationId, started_at as StartedAt,
                       speaker_key as SpeakerKey,
                       row_number() over (partition by task, reason order by rank, started_at, id) as n
                from labeling_items
                where task = any(@tasks) and answered_at is null and (leased_until is null or leased_until <= @now)) o
            where n <= @perReason
            order by Task, Reason, n
            """,
            new { tasks, perReason, now }, cancellationToken: ct))).AsList();
    }

    /// <summary>Leases the items in this order to a new session; one leased or answered meanwhile is left out. Returns those it took.</summary>
    public async Task<long[]> TakeAsync(
        IReadOnlyList<long> ids, string leaseTask, Guid session, DateTimeOffset until, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var taken = await connection.QueryAsync<long>(new CommandDefinition(
            """
            update labeling_items i set lease_task = @leaseTask, lease_session = @session, lease_order = v.n, leased_until = @until
            from unnest(@ids) with ordinality as v(id, n)
            where i.id = v.id and i.answered_at is null and (i.leased_until is null or i.leased_until <= @now)
            returning i.id
            """,
            new { ids = ids.ToArray(), leaseTask, session, until, now }, cancellationToken: ct));
        var set = taken.ToHashSet();
        return [.. ids.Where(set.Contains)];
    }

    /// <summary>An item with its lines and up to <see cref="ContextLines"/> lines within <paramref name="context"/> either side, times in milliseconds from its start.</summary>
    public sealed record ItemView(
        long Id, string Task, string Reason, Guid ConversationId, DateTime StartedAt, DateTime EndedAt, string? GuessModel,
        string? GuessValue, float? GuessScore, float[]? Peaks, string? SpeakerKey, IReadOnlyList<ItemLine> Lines,
        IReadOnlyList<ItemLine> ContextBefore, IReadOnlyList<ItemLine> ContextAfter);

    public sealed record ItemLine(long SegmentId, string Text, bool IsUser, long StartMs, long EndMs);

    /// <summary>A class: Dapper cannot match a constructor to an array column.</summary>
    private sealed class ItemRow
    {
        public long Id { get; set; }

        public string Task { get; set; } = "";

        public string Reason { get; set; } = "";

        public Guid ConversationId { get; set; }

        public DateTime StartedAt { get; set; }

        public DateTime EndedAt { get; set; }

        public string? GuessModel { get; set; }

        public string? GuessValue { get; set; }

        public float? GuessScore { get; set; }

        public float[]? Peaks { get; set; }

        public string? SpeakerKey { get; set; }

        public long[] SegmentIds { get; set; } = [];

        public string[]? Source { get; set; }

        public long? ItemId { get; set; }

        public long? SupersededBy { get; set; }
    }

    private sealed record LineRow(long Id, Guid ConversationId, DateTime StartedAt, DateTime EndedAt, string Text, bool IsUser);

    /// <summary>
    /// The items in the order of <paramref name="ids"/>. An item none of whose lines exists any more (a paid pass replaced them) is
    /// deleted and left out.
    /// </summary>
    public async Task<IReadOnlyList<ItemView>> ItemsAsync(IReadOnlyList<long> ids, TimeSpan context, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var items = (await connection.QueryAsync<ItemRow>(new CommandDefinition(
            """
            select id as Id, task as Task, reason as Reason, conversation_id as ConversationId, started_at as StartedAt, ended_at as EndedAt,
                   guess_model as GuessModel, guess_value as GuessValue, guess_score as GuessScore, peaks as Peaks,
                   speaker_key as SpeakerKey, segment_ids as SegmentIds
            from labeling_items where id = any(@ids)
            """,
            new { ids = ids.ToArray() }, cancellationToken: ct))).ToDictionary(i => i.Id);
        if (items.Count == 0)
        {
            return [];
        }

        var conversations = items.Values.Select(i => i.ConversationId).Distinct().ToArray();
        var lines = (await connection.QueryAsync<LineRow>(new CommandDefinition(
            $"""
            select s.id as Id, s.conversation_id as ConversationId, s.started_at as StartedAt, s.ended_at as EndedAt, s.text as Text,
                   coalesce({SpeakerLabel.IsUser}, false) as IsUser
            from segments s
            where s.conversation_id = any(@conversations)
              and exists (select 1 from labeling_items i where i.id = any(@ids) and i.conversation_id = s.conversation_id
                          and s.started_at < i.ended_at + @context and s.ended_at > i.started_at - @context)
            order by s.started_at, s.id
            """,
            new { conversations, ids = ids.ToArray(), context }, cancellationToken: ct))).ToList();

        // A boundary item can span two conversations: the line outside the first one's is read by its id.
        var seen = lines.Select(l => l.Id).ToHashSet();
        var elsewhere = items.Values.Where(i => i.Task == LabelingTasks.Boundary).SelectMany(i => i.SegmentIds).Where(id => !seen.Contains(id)).ToArray();
        if (elsewhere.Length > 0)
        {
            lines.AddRange(await connection.QueryAsync<LineRow>(new CommandDefinition(
                $"""
                select s.id as Id, s.conversation_id as ConversationId, s.started_at as StartedAt, s.ended_at as EndedAt, s.text as Text,
                       coalesce({SpeakerLabel.IsUser}, false) as IsUser
                from segments s where s.id = any(@elsewhere)
                """,
                new { elsewhere }, cancellationToken: ct)));
        }

        var views = new List<ItemView>();
        var gone = new List<long>();
        foreach (var id in ids)
        {
            if (!items.TryGetValue(id, out var item))
            {
                continue;
            }

            var own = item.SegmentIds.ToHashSet();
            var near = lines.Where(l => l.ConversationId == item.ConversationId
                && l.StartedAt < item.EndedAt + context && l.EndedAt > item.StartedAt - context).ToList();
            var mine = lines.Where(l => own.Contains(l.Id)).OrderBy(l => l.StartedAt).ThenBy(l => l.Id).Select(l => Line(l, item.StartedAt)).ToList();
            if (mine.Count == 0)
            {
                gone.Add(id);
                continue;
            }

            var others = near.Where(l => !own.Contains(l.Id)).ToList();
            views.Add(new ItemView(
                item.Id, item.Task, item.Reason, item.ConversationId, item.StartedAt, item.EndedAt, item.GuessModel, item.GuessValue,
                item.GuessScore, item.Peaks, item.SpeakerKey, mine,
                [.. others.Where(l => l.StartedAt < item.StartedAt).TakeLast(ContextLines).Select(l => Line(l, item.StartedAt))],
                [.. others.Where(l => l.StartedAt >= item.StartedAt).Take(ContextLines).Select(l => Line(l, item.StartedAt))]));
        }

        if (gone.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from labeling_items where id = any(@gone) and not exists (select 1 from labels l where l.item_id = labeling_items.id)",
                new { gone }, cancellationToken: ct));
        }

        return views;
    }

    private static ItemLine Line(LineRow l, DateTime start) =>
        new(l.Id, l.Text, l.IsUser, (long)(l.StartedAt - start).TotalMilliseconds, (long)(l.EndedAt - start).TotalMilliseconds);

    /// <summary>The conversation and capture times of an item, for its clip; null for an unknown one.</summary>
    public async Task<(Guid ConversationId, DateTime StartedAt, DateTime EndedAt)?> ItemSpanAsync(long id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var item = await connection.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            "select conversation_id as ConversationId, started_at as StartedAt, ended_at as EndedAt from labeling_items where id = @id",
            new { id }, cancellationToken: ct));
        return item is null ? null : (item.ConversationId, item.StartedAt, item.EndedAt);
    }

    /// <summary>How many lines an item holds, null for an unknown one.</summary>
    public async Task<int?> ItemLineCountAsync(long id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "select cardinality(segment_ids) from labeling_items where id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>The kept clip of an item's current label, if one was kept.</summary>
    public async Task<byte[]?> KeptClipAsync(long itemId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<byte[]?>(new CommandDefinition(
            """
            select c.body from labels l join label_clips c on c.label_id = l.id
            where l.item_id = @itemId and l.superseded_by is null and c.body is not null
            order by l.id desc limit 1
            """,
            new { itemId }, cancellationToken: ct));
    }

    /// <summary>Keeps a label's clip (<c>labeling.keepClips</c>); a label deleted meanwhile keeps nothing.</summary>
    public async Task KeepClipAsync(long labelId, byte[] body, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into label_clips (label_id, body, created_at)
            select @labelId, @body, @now where exists (select 1 from labels where id = @labelId)
            on conflict (label_id) do update set body = excluded.body
            """,
            new { labelId, body, now }, cancellationToken: ct));
    }

    /// <summary>Drops every kept clip's audio, keeping the row and its embedding: retention does this while <c>labeling.keepClips</c> is off.</summary>
    public async Task<int> DropClipBodiesAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "update label_clips set body = null where body is not null", cancellationToken: ct));
    }

    /// <summary>Returns a skipped item to the queue. False for an unknown item.</summary>
    public async Task<bool> SkipAsync(long itemId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            update labeling_items set lease_task = null, lease_session = null, lease_order = null, leased_until = null
            where id = @itemId
            """,
            new { itemId }, cancellationToken: ct)) > 0
            || await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "select exists (select 1 from labeling_items where id = @itemId)", new { itemId }, cancellationToken: ct));
    }

    /// <summary>The label a client id already made, if any.</summary>
    public async Task<long?> ByClientAsync(Guid clientId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "select id from labels where client_id = @clientId", new { clientId }, cancellationToken: ct));
    }

    /// <summary>
    /// Stores an answer to an item: the label, the supersession of the item's current label, the item answered, and the marks
    /// (see <see cref="WriteThroughAsync"/>), in one transaction. A client id seen before returns its label and changes nothing.
    /// </summary>
    public async Task<LabelAnswer> AnswerAsync(long itemId, NewLabel label, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var item = await connection.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            """
            select id as Id, task as Task, conversation_id as ConversationId, segment_ids as SegmentIds, started_at as StartedAt, ended_at as EndedAt
            from labeling_items where id = @itemId for update
            """,
            new { itemId }, transaction, cancellationToken: ct));
        if (item is null)
        {
            return new LabelAnswer(false, null, null);
        }

        // Under the item's lock: the same client id racing itself waits here and then finds its label.
        if (label.ClientId is { } clientId && await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                "select id from labels where client_id = @clientId", new { clientId }, transaction, cancellationToken: ct)) is { } seen)
        {
            return new LabelAnswer(true, seen, null);
        }

        var previous = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            "select id from labels where item_id = @itemId and superseded_by is null order by id desc limit 1 for update",
            new { itemId }, transaction, cancellationToken: ct));
        var id = await InsertAsync(connection, transaction, item.Id, item.Task, item.ConversationId, item.SegmentIds, item.StartedAt, item.EndedAt, label, now, ct);
        if (previous is { } old)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "update labels set superseded_by = @id where id = @old", new { id, old }, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            update labeling_items set answered_at = @now, lease_task = null, lease_session = null, lease_order = null, leased_until = null
            where id = @itemId
            """,
            new { itemId, now }, transaction, cancellationToken: ct));
        await WriteThroughAsync(connection, transaction, id, item.SegmentIds, label.Source, previous, ct);
        await transaction.CommitAsync(ct);
        return new LabelAnswer(true, id, previous, Created: true);
    }

    private static Task<long> InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long? itemId, string? task, Guid conversationId, long[] segmentIds,
        DateTime startedAt, DateTime endedAt, NewLabel l, DateTimeOffset createdAt, CancellationToken ct) =>
        connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            insert into labels (item_id, task, conversation_id, segment_ids, started_at, ended_at, source, device_type, content, transcript,
                                flags, language, person_id, correction, overlap, lines_differ, listened_ms, answered_with, taxonomy_version,
                                provenance, client_id, created_at)
            values (@itemId, @task, @conversationId, @segmentIds, @startedAt, @endedAt, @Source, @DeviceType, @Content, @Transcript,
                    @Flags, @Language, @PersonId, @Correction, @Overlap, @LinesDiffer, @ListenedMs, @AnsweredWith, @version,
                    @Provenance, @ClientId, @createdAt)
            returning id
            """,
            new
            {
                itemId, task, conversationId, segmentIds, startedAt, endedAt, l.Source, l.DeviceType, l.Content, l.Transcript, l.Flags,
                l.Language, l.PersonId, l.Correction, l.Overlap, l.LinesDiffer, l.ListenedMs, l.AnsweredWith, version = TaxonomyVersion,
                l.Provenance, l.ClientId, createdAt,
            },
            transaction, cancellationToken: ct));

    /// <summary>
    /// The marks a source sets: <c>me</c> and <c>not_me</c> the wearer's own mark, <c>live</c>, <c>device</c> and <c>call</c> the speech
    /// kind (<c>device</c> as <c>media</c> until a device kind exists). Two sources ("Several") and every other value set nothing.
    /// </summary>
    public static (string? Voice, string? Speech) Marks(string[]? source) => source is [var only]
        ? only switch
        {
            "me" => ("true", null),
            "not_me" => ("false", null),
            "live" => (null, SpeechKinds.Person),
            "device" => (null, SpeechKinds.Media),
            "call" => (null, SpeechKinds.Call),
            _ => (null, null),
        }
        : (null, null);

    /// <summary>
    /// Makes the segments' marks follow label <paramref name="labelId"/>: each mark its source sets is set and recorded as set by it;
    /// a mark the label replacing it (<paramref name="previous"/>) had set and this one does not is cleared, if it still holds that
    /// value. A <c>me</c> mark teaches the voiceprint nothing here: only <see cref="ConfirmVoiceAsync"/> does.
    /// </summary>
    private static async Task WriteThroughAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long labelId, long[] segmentIds, string[]? source, long? previous,
        CancellationToken ct)
    {
        var (voice, speech) = Marks(source);
        foreach (var (mark, value) in new[] { ("voice", voice), ("speech", speech) })
        {
            if (value is not null)
            {
                var existing = (await connection.QueryAsync<long>(new CommandDefinition(
                    "select id from segments where id = any(@segmentIds) order by id for update", new { segmentIds }, transaction, cancellationToken: ct))).ToArray();
                await SetMarkAsync(connection, transaction, mark, existing, value, ct);
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into label_marks (segment_id, mark, label_id, value)
                    select unnest(@existing), @mark, @labelId, @value
                    on conflict (segment_id, mark) do update set label_id = excluded.label_id, value = excluded.value
                    """,
                    new { existing, mark, labelId, value }, transaction, cancellationToken: ct));
            }
            else if (previous is { } old)
            {
                await ClearMarksAsync(connection, transaction, old, mark, ct);
            }
        }
    }

    /// <summary>Clears the marks of <paramref name="mark"/> the label set that still hold its value, and forgets them.</summary>
    private static async Task ClearMarksAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long labelId, string mark, CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<(long SegmentId, string Value)>(new CommandDefinition(
            "delete from label_marks where label_id = @labelId and mark = @mark returning segment_id, value",
            new { labelId, mark }, transaction, cancellationToken: ct))).ToList();
        foreach (var group in rows.GroupBy(r => r.Value))
        {
            var ids = group.Select(r => r.SegmentId).ToArray();
            var holding = (await connection.QueryAsync<long>(new CommandDefinition(
                mark == "voice"
                    ? "select id from segments where id = any(@ids) and is_user_manual = cast(@value as boolean)"
                    : "select id from segments where id = any(@ids) and speech_manual = @value",
                new { ids, value = group.Key }, transaction, cancellationToken: ct))).ToArray();
            await SetMarkAsync(connection, transaction, mark, holding, null, ct);
        }
    }

    private static async Task SetMarkAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string mark, long[] ids, string? value, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return;
        }

        if (mark == "voice")
        {
            await VoiceStore.SetMarksAsync(connection, transaction, ids, value is null ? null : value == "true", ct);
        }
        else
        {
            await SpeechStore.MarkAsync(connection, transaction, ids, value, ct);
        }
    }

    /// <summary>
    /// Deletes a label and its kept clip and clears every mark it set. The label it had superseded becomes current again and its
    /// marks apply; an item left without a label returns to the queue. False for an unknown label.
    /// </summary>
    public async Task<bool> DeleteAsync(long labelId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var l = await connection.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            "select id as Id, item_id as ItemId, superseded_by as SupersededBy from labels where id = @labelId for update",
            new { labelId }, transaction, cancellationToken: ct));
        if (l is null)
        {
            return false;
        }

        await ClearMarksAsync(connection, transaction, labelId, "voice", ct);
        await ClearMarksAsync(connection, transaction, labelId, "speech", ct);
        var restored = (await connection.QueryAsync<ItemRow>(new CommandDefinition(
            "update labels set superseded_by = @next where superseded_by = @labelId returning id as Id, segment_ids as SegmentIds, source as Source",
            new { labelId, next = l.SupersededBy }, transaction, cancellationToken: ct))).ToList();
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from labels where id = @labelId", new { labelId }, transaction, cancellationToken: ct));
        if (l.SupersededBy is null)
        {
            foreach (var p in restored)
            {
                await WriteThroughAsync(connection, transaction, p.Id, p.SegmentIds, p.Source, null, ct);
            }
        }

        if (l.ItemId is { } itemId)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "update labeling_items set answered_at = null where id = @itemId and not exists (select 1 from labels where item_id = @itemId)",
                new { itemId }, transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Stores imported labels with no item and no marks. An item whose lines are not all there, span conversations, or that the same
    /// provenance already imported is skipped. Creation times step by a microsecond, so <c>GET /labels</c> pages through them.
    /// </summary>
    public async Task<(int Imported, int Skipped)> ImportAsync(string provenance, IReadOnlyList<ImportLabel> items, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        int imported = 0, skipped = 0;
        foreach (var item in items)
        {
            var ids = item.SegmentIds.Distinct().Order().ToArray();
            var span = await connection.QuerySingleAsync<ImportSpan>(new CommandDefinition(
                """
                select count(*)::int as Count, count(distinct conversation_id)::int as Conversations,
                       min(conversation_id::text)::uuid as ConversationId, min(started_at) as "From", max(ended_at) as Until
                from segments where id = any(@ids)
                """,
                new { ids }, transaction, cancellationToken: ct));
            if (span.Count != ids.Length || span.Conversations != 1 || await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "select exists (select 1 from labels where provenance = @provenance and segment_ids = @ids)",
                    new { provenance, ids }, transaction, cancellationToken: ct)))
            {
                skipped++;
                continue;
            }

            var label = new NewLabel(null, item.Source, null, item.Content, item.Transcript, item.Flags, null, null, null, null, false, null, null, provenance);
            await InsertAsync(
                connection, transaction, null, null, span.ConversationId!.Value, ids, span.From!.Value, span.Until!.Value, label,
                now.AddTicks(imported * 10), ct);
            imported++;
        }

        await transaction.CommitAsync(ct);
        return (imported, skipped);
    }

    private sealed record ImportSpan(int Count, int Conversations, Guid? ConversationId, DateTime? From, DateTime? Until);

    /// <summary>
    /// The lines of current, unconfirmed app labels of exactly <c>me</c> whose mark still stands, oldest first. A label flagged two
    /// speakers or overlapping voices is left out: its line holds another voice.
    /// </summary>
    public async Task<IReadOnlyList<PendingVoiceLine>> PendingVoiceAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<PendingVoiceLine>(new CommandDefinition(
            """
            select l.id as LabelId, s.id as SegmentId, (extract(epoch from s.ended_at - s.started_at) * 1000)::int as DurationMs
            from labels l
            join label_marks m on m.label_id = l.id and m.mark = 'voice' and m.value = 'true'
            join segments s on s.id = m.segment_id
            where l.superseded_by is null and l.provenance = 'app' and l.confirmed_at is null and l.source = '{me}'
              and not (l.flags && array['two_speakers', 'overlap']) and s.is_user_manual
            order by l.created_at, l.id, s.started_at
            limit @limit
            """,
            new { limit }, cancellationToken: ct))).AsList();
    }

    /// <summary>
    /// Lets the labels teach the voiceprint: the lines <see cref="PendingVoiceAsync"/> would list for them, when <paramref name="learn"/>
    /// (<c>voice.learn</c>), each <paramref name="learnSeconds"/> or longer with a fingerprint still held. Every label is marked
    /// confirmed; one confirmed before teaches nothing again. Null when a label id is unknown, with nothing changed.
    /// </summary>
    public async Task<int?> ConfirmVoiceAsync(
        long[] labelIds, bool learn, double learnSeconds, float newPrintBelow, int maxPrints, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var ids = labelIds.Distinct().ToArray();
        var fresh = (await connection.QueryAsync<(long Id, bool Confirmed)>(new CommandDefinition(
            "select id, confirmed_at is not null from labels where id = any(@ids) order by id for update",
            new { ids }, transaction, cancellationToken: ct))).ToList();
        if (fresh.Count != ids.Length)
        {
            return null;
        }

        var open = fresh.Where(f => !f.Confirmed).Select(f => f.Id).ToArray();
        var segments = (await connection.QueryAsync<long>(new CommandDefinition(
            """
            select m.segment_id from labels l join label_marks m on m.label_id = l.id and m.mark = 'voice' and m.value = 'true'
            where l.id = any(@open) and l.superseded_by is null and l.source = '{me}' and not (l.flags && array['two_speakers', 'overlap'])
            """,
            new { open }, transaction, cancellationToken: ct))).ToArray();
        var learned = learn && segments.Length > 0
            ? await VoiceStore.LearnMarkedAsync(connection, transaction, segments, learnSeconds, newPrintBelow, maxPrints, ct)
            : 0;
        await connection.ExecuteAsync(new CommandDefinition(
            "update labels set confirmed_at = @now where id = any(@open)", new { open, now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return learned;
    }

    /// <summary>A label as <c>GET /labels</c> lists it, with the models' own verdicts on its lines. A class for its <c>text[]</c> columns.</summary>
    public sealed class LabelRow
    {
        public long LabelId { get; set; }

        public long? ItemId { get; set; }

        public string? Task { get; set; }

        public long[] SegmentIds { get; set; } = [];

        public Guid ConversationId { get; set; }

        public DateTime StartedAt { get; set; }

        public int DurationMs { get; set; }

        public string[]? Source { get; set; }

        public string? DeviceType { get; set; }

        public string? Content { get; set; }

        public string? Transcript { get; set; }

        public string[] Flags { get; set; } = [];

        public string? Language { get; set; }

        public Guid? PersonId { get; set; }

        public string? Correction { get; set; }

        public string? Overlap { get; set; }

        public string? AnsweredWith { get; set; }

        public string Provenance { get; set; } = "";

        public DateTime CreatedAt { get; set; }
    }

    /// <summary>The model's verdicts on a labelled line, never the rule's: once a label is a mark, the rule only repeats it.</summary>
    public sealed record LineVerdict(long SegmentId, bool? VoiceIsUser, float? VoiceSimilarity, string? SpeechGuess, float? SpeechScore);

    /// <summary>Current labels created after <paramref name="since"/>, oldest first, optionally of one task (<c>import</c> for imports).</summary>
    public async Task<IReadOnlyList<LabelRow>> ListAsync(DateTimeOffset? since, string? task, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<LabelRow>(new CommandDefinition(
            """
            select id as LabelId, item_id as ItemId, task as Task, segment_ids as SegmentIds, conversation_id as ConversationId,
                   started_at as StartedAt, (extract(epoch from ended_at - started_at) * 1000)::int as DurationMs, source as Source,
                   device_type as DeviceType, content as Content, transcript as Transcript, flags as Flags, language as Language,
                   person_id as PersonId, correction as Correction, overlap as Overlap, answered_with as AnsweredWith,
                   provenance as Provenance, created_at as CreatedAt
            from labels
            where superseded_by is null
              and (cast(@since as timestamptz) is null or created_at > cast(@since as timestamptz))
              and (cast(@task as text) is null or task = @task or (@task = 'import' and task is null))
            order by created_at, id
            limit @limit
            """,
            new { since, task, limit }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<LineVerdict>> VerdictsAsync(long[] segmentIds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<LineVerdict>(new CommandDefinition(
            """
            select id as SegmentId, voice_is_user as VoiceIsUser, voice_similarity as VoiceSimilarity, speech_guess as SpeechGuess,
                   speech_score as SpeechScore
            from segments where id = any(@segmentIds)
            """,
            new { segmentIds }, cancellationToken: ct))).AsList();
    }

    /// <summary>Per task: open items, current labels, and the labels that count towards its goal (see <see cref="GoalSql"/>).</summary>
    public async Task<IReadOnlyList<(string Task, int Open, int Labelled, int Progress)>> TaskCountsAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<(string, int, int, int)>(new CommandDefinition(
            $"""
            select t.task,
                   (select count(*)::int from labeling_items i where i.task = t.task and i.answered_at is null),
                   (select count(*)::int from labels l where l.task = t.task and l.superseded_by is null),
                   (select count(*)::int from labels l where l.task = t.task and l.superseded_by is null and {GoalSql})
            from unnest(@tasks) as t(task)
            """,
            new { tasks = LabelingTasks.All }, cancellationToken: ct))).AsList();
    }

    /// <summary>
    /// SQL for whether a label aliased <c>l</c> counts towards its task's goal: for <c>device</c>, a not-device line (the detector's
    /// ship gate needs 100 of them); for <c>voice</c>, a clear <c>me</c> or <c>not_me</c>; else any answer.
    /// </summary>
    private const string GoalSql =
        "case l.task when 'device' then l.source && array['me', 'live', 'call'] and not (l.source && array['device']) "
        + "when 'voice' then l.source && array['me', 'not_me'] and cardinality(l.source) = 1 else true end";

    /// <summary>Moves items and labels of merged conversations to the survivor, inside the merge's transaction.</summary>
    public static async Task MoveConversationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid[] from, Guid to, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update labeling_items set conversation_id = @to where conversation_id = any(@from);
            update labels set conversation_id = @to where conversation_id = any(@from);
            """,
            new { from, to }, transaction, cancellationToken: ct));
    }

    /// <summary>SQL for the opaque key of a segment aliased <c>s</c>'s voice, joined to its fingerprint <c>f</c>: its voice group, else the provider's label.</summary>
    internal const string SpeakerKey =
        "coalesce('g:' || f.group_id::text, 's:' || nullif(s.speaker_id, ''), 's:' || nullif(s.speaker, ''))";
}

/// <summary>The labelling tasks and the axis each asks.</summary>
public static class LabelingTasks
{
    public const string Device = "device";
    public const string Voice = "voice";
    public const string Quiet = "quiet";
    public const string Transcript = "transcript";
    public const string Overlap = "overlap";

    /// <summary>The golden kinds (<see cref="GoldenStore"/>): asked only for the days in <c>eval.goldenDays</c>, in no mix.</summary>
    public const string Speaker = "speaker";

    public const string Boundary = "boundary";

    /// <summary>Not a task: <c>GET /next?task=mixed</c> interleaves them all.</summary>
    public const string Mixed = "mixed";

    public static readonly string[] All = [Device, Voice, Quiet, Transcript, Overlap];

    public static readonly string[] Golden = [Speaker, Boundary];

    public static bool IsGolden(string task) => Golden.Contains(task);

    public static string Ask(string task) => task switch
    {
        Device or Voice => "source",
        Overlap => "overlap",
        Speaker => "speaker",
        Boundary => "boundary",
        _ => "transcript",
    };
}
