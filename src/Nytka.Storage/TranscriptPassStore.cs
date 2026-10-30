using Dapper;
using Npgsql;

namespace Nytka.Storage;

public static class PassStatus
{
    public const string Planned = "planned";
    public const string Submitted = "submitted";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Stale = "stale";
    public const string Waiting = "waiting";
}

public sealed record NewPass(Guid ConversationId, string Reason, IReadOnlyList<long> BatchIds, string StretchesJson, string OffsetMapJson, int AudioMs);

/// <summary>A pass as the handler works on it. A class, not a record: Dapper cannot pass a <c>bigint[]</c> to a constructor parameter.</summary>
public sealed class PassRow
{
    public long Id { get; init; }

    public Guid ConversationId { get; init; }

    public string Reason { get; init; } = "";

    public string Status { get; init; } = "";

    public string? Engine { get; init; }

    public string? ExternalId { get; init; }

    public string? ExternalFileId { get; init; }

    public long[] BatchIds { get; init; } = [];

    public string Stretches { get; init; } = "[]";

    public string OffsetMap { get; init; } = "";

    public int AudioMs { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime? SubmittedAt { get; init; }

    public DateTime? FinishedAt { get; init; }
}

/// <summary>What the app shows of a conversation's latest pass.</summary>
public sealed record PassSummary(string Status, string Reason, string? Engine, DateTime? FinishedAt);

/// <summary>A finished batch of a conversation that holds speech audio, for planning a pass.</summary>
public sealed record PassBatch(long Id, DateTime StartedAt, DateTime EndedAt);

/// <summary>One paid line, ready to be stored.</summary>
public sealed record PaidSegment(
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Text, string? Speaker, string? SpeakerId, SegmentVoice? Voice,
    SegmentPrints? Prints = null);

public enum SwapStatus
{
    Done,

    /// <summary>The conversation changed under the pass (a covered batch moved or went); the pass is marked stale and a new plan is needed.</summary>
    Stale,

    /// <summary>The pass or its conversation is gone, or the pass already finished: nothing was written.</summary>
    NotCurrent,
}

public sealed record SwapResult(SwapStatus Status, int Replaced = 0, int Inserted = 0, string Tier = "draft");

/// <summary>Runs inside <see cref="TranscriptPassStore.SwapAsync"/>'s transaction once the lines are swapped, so what it writes commits with them.</summary>
public delegate Task SwapHook(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, long passId, int replaced, int inserted, string tier, CancellationToken ct);

/// <summary>Transcript passes (docs/specs/auto-transcription.md): one paid request per conversation piece, and the atomic swap of its text for the draft.</summary>
public sealed class TranscriptPassStore(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// A finished batch (aliased <c>b</c>) a pass could still pay for: no pass covered it and its speech audio is still held. The one test
    /// the route, the plan and the tier share.
    /// </summary>
    public const string Uncovered = "b.status = 'done' and b.pass_id is null and exists (select 1 from speech_audio a where a.batch_id = b.id)";

    /// <summary>A batch whose text is still a draft or on its way: <see cref="Uncovered"/>, or not finished yet. A batch without audio or a failed one is not.</summary>
    public const string Unfinished = $"b.status = 'pending' or ({Uncovered})";

    private const string PassSelect =
        """
        select id as Id, conversation_id as ConversationId, reason as Reason, status as Status, engine as Engine,
               external_id as ExternalId, external_file_id as ExternalFileId, batch_ids as BatchIds, stretches::text as Stretches,
               offset_map::text as OffsetMap, audio_ms as AudioMs, created_at as CreatedAt, submitted_at as SubmittedAt,
               finished_at as FinishedAt
        from transcript_passes
        """;

    /// <summary>Writes a <c>planned</c> pass. Null when the conversation already has a planned or submitted one (or is gone).</summary>
    public async Task<long?> CreateAsync(NewPass pass, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            insert into transcript_passes (conversation_id, reason, status, batch_ids, stretches, offset_map, audio_ms, created_at)
            select c.id, @Reason, 'planned', @BatchIds, cast(@StretchesJson as jsonb), cast(@OffsetMapJson as jsonb), @AudioMs, @now
            from conversations c where c.id = @ConversationId
            on conflict (conversation_id) where status in ('planned', 'submitted') do nothing
            returning id
            """,
            new { pass.ConversationId, pass.Reason, BatchIds = pass.BatchIds.ToArray(), pass.StretchesJson, pass.OffsetMapJson, pass.AudioMs, now },
            cancellationToken: ct));
    }

    public async Task<PassRow?> GetAsync(long id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PassRow>(new CommandDefinition(
            $"{PassSelect} where id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>The conversation's planned or submitted pass, if any: at most one exists.</summary>
    public async Task<PassRow?> ActiveAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PassRow>(new CommandDefinition(
            $"{PassSelect} where conversation_id = @conversationId and status in ('planned', 'submitted')",
            new { conversationId }, cancellationToken: ct));
    }

    public async Task<PassSummary?> LatestAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PassSummary>(new CommandDefinition(
            """
            select status as Status, reason as Reason, engine as Engine, finished_at as FinishedAt
            from transcript_passes where conversation_id = @conversationId
            order by id desc limit 1
            """,
            new { conversationId }, cancellationToken: ct));
    }

    /// <summary>The finished batches no pass covered that still hold speech audio, in capture order.</summary>
    public async Task<IReadOnlyList<PassBatch>> UncoveredBatchesAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<PassBatch>(new CommandDefinition(
            $"""
            select b.id as Id, b.started_at as StartedAt, b.ended_at as EndedAt
            from transcription_batches b
            where b.conversation_id = @conversationId and {Uncovered}
            order by b.started_at, b.id
            """,
            new { conversationId }, cancellationToken: ct))).AsList();
    }

    /// <summary>The Opus bodies of the batches' speech audio in capture order.</summary>
    public async Task<IReadOnlyList<byte[]>> SpeechBodiesAsync(IReadOnlyList<long> batchIds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<byte[]>(new CommandDefinition(
            "select body from speech_audio where batch_id = any(@batchIds) order by started_at, id",
            new { batchIds = batchIds.ToArray() }, cancellationToken: ct))).AsList();
    }

    /// <summary>The draft text length of the batches' lines, to estimate what the paid text will cost.</summary>
    public async Task<int> DraftCharsAsync(IReadOnlyList<long> batchIds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select coalesce(sum(length(text)), 0)::int from segments where batch_id = any(@batchIds) and pass_id is null",
            new { batchIds = batchIds.ToArray() }, cancellationToken: ct));
    }

    /// <summary>Whether the conversation holds a finished batch that no pass has covered, audio or not.</summary>
    public async Task<bool> HasDraftOnlyAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from transcription_batches where conversation_id = @conversationId and status = 'done' and pass_id is null)",
            new { conversationId }, cancellationToken: ct));
    }

    public async Task SubmittedAsync(long id, string engine, string? externalId, string? externalFileId, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update transcript_passes
            set status = 'submitted', engine = @engine, external_id = @externalId, external_file_id = @externalFileId,
                submitted_at = coalesce(submitted_at, @now)
            where id = @id and status in ('planned', 'submitted')
            """,
            new { id, engine, externalId, externalFileId, now }, cancellationToken: ct));
    }

    /// <summary>Ends an active pass with <paramref name="status"/> (<c>failed</c>, <c>stale</c> or <c>waiting</c>) and the fixed sentence that says why.</summary>
    public async Task EndAsync(long id, string status, string? error, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update transcript_passes
            set status = @status, error = @error, finished_at = @now, external_id = null, external_file_id = null
            where id = @id and status in ('planned', 'submitted')
            """,
            new { id, status, error, now }, cancellationToken: ct));
    }

    /// <summary>
    /// Replaces the draft lines of the pass's batches that the paid lines overlap with the paid lines, in one transaction from the
    /// check that the pass is still current to <paramref name="recorded"/> (the ledger row and the event): a crash or an exception
    /// leaves the whole draft, a commit leaves the whole swap. <paramref name="enrich"/> queues the summary again; otherwise it
    /// only forgets the old one, for the scheduler to find. <paramref name="stale"/> runs instead of <paramref name="recorded"/> when the
    /// pass turns out stale: the request was paid for all the same.
    /// </summary>
    public async Task<SwapResult> SwapAsync(
        PassRow row, IReadOnlyList<PaidSegment> paid, string engine, bool enrich, DateTimeOffset now, CancellationToken ct,
        SwapHook? recorded = null, SwapHook? stale = null)
    {
        var passId = row.Id;
        var conversationId = row.ConversationId;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // The request was paid for whatever became of the pass: the ledger row is written on every path out of here but a failure.
        async Task<SwapResult> SpentAsync(SwapStatus status)
        {
            if (stale is not null)
            {
                await stale(connection, transaction, conversationId, passId, 0, 0, "draft", ct);
            }

            // The conversation is gone: nothing is left to hold the pass, and the job has cleaned up at the engine.
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from transcript_passes where id = @passId and not exists (select 1 from conversations where id = @conversationId)",
                new { passId, conversationId }, transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
            return new SwapResult(status);
        }

        // The conversation first, as a merge takes it, then the pass.
        var exists = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            "select id from conversations where id = @conversationId for update", new { conversationId }, transaction, cancellationToken: ct));
        var pass = exists is null
            ? null
            : await connection.QuerySingleOrDefaultAsync<PassRow>(new CommandDefinition(
                $"{PassSelect} where id = @passId and status in ('planned', 'submitted') for update", new { passId }, transaction, cancellationToken: ct));
        if (pass is null)
        {
            return await SpentAsync(SwapStatus.NotCurrent);
        }

        var batches = (await connection.QueryAsync<PassBatch>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, ended_at as EndedAt
            from transcription_batches
            where id = any(@ids) and conversation_id = @conversationId and status = 'done' and pass_id is null
            order by started_at, id
            """,
            new { ids = pass.BatchIds, conversationId }, transaction, cancellationToken: ct))).AsList();
        if (batches.Count != pass.BatchIds.Length)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "update transcript_passes set status = 'stale', finished_at = @now, external_id = null, external_file_id = null where id = @passId",
                new { passId, now }, transaction, cancellationToken: ct));
            return await SpentAsync(SwapStatus.Stale);
        }

        var drafts = (await connection.QueryAsync<DraftRow>(new CommandDefinition(
            """
            select s.id as Id, s.started_at as StartedAt, s.ended_at as EndedAt, s.text as Text, s.speaker_id as SpeakerId,
                   s.is_user_manual as IsUserManual, s.speech_manual as SpeechManual, s.person_id as PersonId, u.engine as Engine
            from segments s left join transcription_usage u on u.batch_id = s.batch_id
            where s.conversation_id = @conversationId and s.batch_id = any(@ids) and s.pass_id is null
            order by s.started_at, s.id
            """,
            new { conversationId, ids = pass.BatchIds }, transaction, cancellationToken: ct))).AsList();

        var ordered = paid.OrderBy(p => p.StartedAt).ThenBy(p => p.EndedAt).ToList();
        var map = TranscriptSwapMap.Build(
            [.. drafts.Select(d => new DraftLine(d.Id, Ms(d.StartedAt), Ms(d.EndedAt), d.SpeakerId, d.IsUserManual, d.SpeechManual, d.PersonId))],
            [.. ordered.Select(p => new PaidLine(p.StartedAt.ToUnixTimeMilliseconds(), p.EndedAt.ToUnixTimeMilliseconds(), p.Speaker, p.SpeakerId))]);

        var inherited = await InheritedGroupsAsync(connection, transaction, drafts, map, ordered.Count, ct);
        var newIds = new long[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            var carried = map.Paid[i];
            newIds[i] = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker, speaker_id, is_user_manual,
                                      speech_manual, speech_kind, person_id, pass_id, voice_checked, voice_similarity, voice_is_user)
                values (@conversationId, @batchId, @StartedAt, @EndedAt, @Text, @Speaker, @SpeakerId, @IsUserManual,
                        @SpeechManual, @SpeechManual, @PersonId, @passId, @VoiceChecked, @VoiceSimilarity, @VoiceIsUser)
                returning id
                """,
                new
                {
                    conversationId, batchId = BatchOf(batches, p.StartedAt.ToUnixTimeMilliseconds()), p.StartedAt, p.EndedAt, p.Text, p.Speaker,
                    SpeakerId = carried.SpeakerId, carried.IsUserManual, carried.SpeechManual, carried.PersonId, passId,
                    VoiceChecked = p.Voice is not null, VoiceSimilarity = p.Voice?.Similarity, VoiceIsUser = p.Voice?.IsUser,
                },
                transaction, cancellationToken: ct));
            if (p.Prints is { } prints)
            {
                await VoiceStore.InsertPrintsAsync(connection, transaction, newIds[i], BatchOf(batches, p.StartedAt.ToUnixTimeMilliseconds()), prints, ct);
            }

            if (p.Voice?.Fingerprint is { } fingerprint && await VoiceModelAsync(connection, transaction, ct) is { } model)
            {
                await VoiceStore.InsertFingerprintAsync(connection, transaction, newIds[i], BatchOf(batches, p.StartedAt.ToUnixTimeMilliseconds()), model, fingerprint, ct);
                if (inherited[i] is { } membership)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "update segment_fingerprints set group_id = @GroupId, grouped = true where segment_id = @segmentId",
                        new { membership.GroupId, segmentId = newIds[i] }, transaction, cancellationToken: ct));
                }
            }
        }

        var replacedIds = drafts.Where(d => map.Replaced.ContainsKey(d.Id)).ToList();
        if (replacedIds.Count > 0)
        {
            var oldIds = replacedIds.Select(d => d.Id).ToArray();
            var newFor = replacedIds.Select(d => newIds[map.Replaced[d.Id]]).ToArray();
            await RepointAsync(connection, transaction, conversationId, oldIds, newFor, ct);

            // A paid line sits in the track of the draft it replaces until the day's tracks are built again.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                update segments n set track_id = o.track_id
                from unnest(@oldIds, @newFor) as m (old_id, new_id) join segments o on o.id = m.old_id
                where n.id = m.new_id and o.track_id is not null
                """,
                new { oldIds, newFor }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into segment_drafts (id, conversation_id, pass_id, started_at, ended_at, text, engine)
                select s.id, s.conversation_id, @passId, s.started_at, s.ended_at, s.text, d.engine
                from segments s join unnest(@oldIds, @engines) as d (id, engine) on d.id = s.id
                """,
                new { passId, oldIds, engines = replacedIds.Select(d => d.Engine).ToArray() }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from segments where id = any(@oldIds)", new { oldIds }, transaction, cancellationToken: ct));
        }

        // A group whose every fingerprint went with a replaced line (no voice model to make new ones) goes too, as in retention.
        await VoiceGroupStore.DeleteEmptyGroupsAsync(connection, transaction, ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "update transcription_batches set pass_id = @passId where id = any(@ids)",
            new { passId, ids = pass.BatchIds }, transaction, cancellationToken: ct));
        await ResetRunsAsync(connection, transaction, conversationId, enrich, ct);

        var tier = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"select exists (select 1 from transcription_batches b where b.conversation_id = @conversationId and ({Unfinished}))",
            new { conversationId }, transaction, cancellationToken: ct)) ? "mixed" : "final";
        await connection.ExecuteAsync(new CommandDefinition(
            "update conversations set transcript_tier = @tier, updated_at = @now where id = @conversationId",
            new { tier, now, conversationId }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update transcript_passes
            set status = 'done', engine = @engine, finished_at = @now, error = null, external_id = null, external_file_id = null
            where id = @passId
            """,
            new { passId, engine, now }, transaction, cancellationToken: ct));

        if (recorded is not null)
        {
            await recorded(connection, transaction, conversationId, passId, replacedIds.Count, ordered.Count, tier, ct);
        }

        await transaction.CommitAsync(ct);
        return new SwapResult(SwapStatus.Done, replacedIds.Count, ordered.Count, tier);
    }

    private sealed record Membership(Guid GroupId);

    /// <summary>
    /// For each paid line, the voice group of the first replaced draft line it takes over that had one. The new fingerprint joins that
    /// group already counted (<c>grouped</c>), so the grouping job neither adds it to the centroid a second time nor leaves the
    /// group's count standing on fingerprints that went with the draft.
    /// </summary>
    private static async Task<Membership?[]> InheritedGroupsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<DraftRow> drafts, SwapMap map, int paidCount, CancellationToken ct)
    {
        var inherited = new Membership?[paidCount];
        var ids = drafts.Where(d => map.Replaced.ContainsKey(d.Id)).Select(d => d.Id).ToArray();
        if (ids.Length == 0)
        {
            return inherited;
        }

        var groups = (await connection.QueryAsync<(long SegmentId, Guid GroupId)>(new CommandDefinition(
            "select segment_id, group_id from segment_fingerprints where segment_id = any(@ids) and group_id is not null",
            new { ids }, transaction, cancellationToken: ct))).ToDictionary(g => g.SegmentId, g => g.GroupId);
        foreach (var draft in drafts)
        {
            if (map.Replaced.TryGetValue(draft.Id, out var index) && inherited[index] is null && groups.TryGetValue(draft.Id, out var group))
            {
                inherited[index] = new Membership(group);
            }
        }

        return inherited;
    }

    /// <summary>
    /// The batch a line belongs to: the covered batch that holds its start, else the one nearest to it. Keeps the line in the
    /// batch's retention and cascades, as a draft line was.
    /// </summary>
    private static long BatchOf(IReadOnlyList<PassBatch> batches, long ms)
    {
        PassBatch? best = null;
        var bestDistance = long.MaxValue;
        foreach (var batch in batches)
        {
            var start = Ms(batch.StartedAt);
            var end = Ms(batch.EndedAt);
            var distance = ms < start ? start - ms : ms > end ? ms - end : 0;
            if (distance < bestDistance)
            {
                best = batch;
                bestDistance = distance;
            }
        }

        return best!.Id;
    }

    private static Task<string?> VoiceModelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "select model from voice_profile where id = 1", transaction: transaction, cancellationToken: ct));

    /// <summary>
    /// Points what named a replaced line at the line that replaced it: a name suggestion's evidence and lines, a fact's line and a voice
    /// match's lines. A suggestion keeps its status, so a rejected name stays rejected. Two label suggestions of one name whose first
    /// line became one line would share a unique key: the one that was decided stays, else the older.
    /// </summary>
    private static async Task RepointAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, long[] oldIds, long[] newIds, CancellationToken ct)
    {
        var map = oldIds.Zip(newIds).ToDictionary(p => p.First, p => p.Second);
        long Move(long id) => map.GetValueOrDefault(id, id);
        await TrackStore.RepointAnswersAsync(connection, transaction, oldIds, newIds, ct);
        long[] MoveAll(long[] ids) => [.. ids.Select(Move).Distinct()];

        var suggestions = (await connection.QueryAsync<SuggestionRow>(new CommandDefinition(
            """
            select id as Id, target as Target, coalesce(speaker_id, group_id::text, case when target = 'person' then person_id::text end) as Key, segment_ids as SegmentIds, lower(name) as Name,
                   evidence_segment_id as Evidence, status as Status, created_at as CreatedAt
            from name_suggestions
            where conversation_id = @conversationId
            """,
            new { conversationId }, transaction, cancellationToken: ct))).AsList();

        var moved = suggestions
            .Select(s => (Row: s, Ids: MoveAll(s.SegmentIds), Evidence: Move(s.Evidence)))
            .ToList();
        var drop = new HashSet<Guid>();
        foreach (var group in moved.GroupBy(m => (m.Row.Target, Key: m.Row.Key ?? (m.Ids.Length > 0 ? m.Ids[0].ToString() : ""), m.Row.Name)))
        {
            if (group.Count() > 1)
            {
                var keep = group.OrderBy(m => m.Row.Status == "pending" ? 1 : 0).ThenBy(m => m.Row.CreatedAt).ThenBy(m => m.Row.Id).First();
                foreach (var m in group.Where(m => m.Row.Id != keep.Row.Id))
                {
                    drop.Add(m.Row.Id);
                }
            }
        }

        if (drop.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from name_suggestions where id = any(@ids)", new { ids = drop.ToArray() }, transaction, cancellationToken: ct));
        }

        foreach (var (row, ids, evidence) in moved.Where(m => !drop.Contains(m.Row.Id)))
        {
            if (evidence == row.Evidence && ids.SequenceEqual(row.SegmentIds))
            {
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "update name_suggestions set evidence_segment_id = @evidence, segment_ids = @ids where id = @id",
                new { id = row.Id, evidence, ids }, transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update person_facts f set segment_id = m.replacement from unnest(@oldIds, @newIds) as m (original, replacement) where f.segment_id = m.original",
            new { oldIds, newIds }, transaction, cancellationToken: ct));

        var matches = (await connection.QueryAsync<MatchRow>(new CommandDefinition(
            "select id as Id, segment_ids as SegmentIds from voice_matches where segment_ids && @oldIds",
            new { oldIds }, transaction, cancellationToken: ct))).AsList();
        foreach (var match in matches)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "update voice_matches set segment_ids = @ids where id = @id",
                new { id = match.Id, ids = MoveAll(match.SegmentIds) }, transaction, cancellationToken: ct));
        }
    }

    /// <summary>
    /// The swapped conversation reads differently, as after a merge: the summary, tasks and memories run again on the final text, and
    /// speech kinds are guessed again for the new lines. Names and facts follow the segment ids.
    /// </summary>
    private static async Task ResetRunsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, bool enrich, CancellationToken ct)
    {
        var args = new { conversationId };
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update conversations
            set ai_status = case when @enrich then 'pending' else 'none' end, ai_message = null, ai_through_segment_id = null, ai_failures = 0
            where id = @conversationId
            """,
            new { conversationId, enrich }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "update memory_runs set status = 'pending', through_segment_id = null, failures = 0, message = null where conversation_id = @conversationId",
            args, transaction, cancellationToken: ct));
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private sealed class DraftRow
    {
        public long Id { get; init; }

        public DateTime StartedAt { get; init; }

        public DateTime EndedAt { get; init; }

        public string Text { get; init; } = "";

        public string? SpeakerId { get; init; }

        public bool? IsUserManual { get; init; }

        public string? SpeechManual { get; init; }

        public Guid? PersonId { get; init; }

        public string? Engine { get; init; }
    }

    private sealed class SuggestionRow
    {
        public Guid Id { get; init; }

        public string Target { get; init; } = "";

        public string? Key { get; init; }

        public long[] SegmentIds { get; init; } = [];

        public string Name { get; init; } = "";

        public long Evidence { get; init; }

        public string Status { get; init; } = "";

        public DateTime CreatedAt { get; init; }
    }

    private sealed class MatchRow
    {
        public Guid Id { get; init; }

        public long[] SegmentIds { get; init; } = [];
    }
}
