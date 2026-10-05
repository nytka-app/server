using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A transcript line as the classifier reads it: its times and whether the wearer spoke it (null when unknown).</summary>
public sealed record SpeechLine(long Id, DateTime StartedAt, DateTime EndedAt, bool? IsUser);

/// <summary>
/// What the classifier decided for a line: <see cref="Call"/> when the far side of a call, else the guess follows from
/// <see cref="Score"/> (null for the wearer's own lines, which are <c>person</c>).
/// </summary>
public sealed record LineGuess(long SegmentId, bool Call, float? Score, IReadOnlyList<string> Signals);

/// <summary>
/// The owner's marks on transcript lines and the rule that turns marks and guesses into the kind every reader asks
/// (docs/specs/speech-kind.md, Which kind wins). A mark wins in every mode; the guess counts only while the applied mode is
/// <c>on</c>. <c>speech_state</c> records the mode and threshold the stored guesses and kinds follow.
/// </summary>
public sealed class SpeechStore(NpgsqlDataSource dataSource)
{
    private static readonly string KindRule = SpeechKinds.Rule("s.speech_manual", "@mode");

    private static readonly string MarkRule = SpeechKinds.Rule("@kind", SpeechKinds.AppliedMode);

    /// <summary>
    /// Sets the owner's mark on a segment: <c>person</c>, <c>media</c>, <c>call</c>, or null to clear it, and derives the segment's
    /// kind in the same statement from the mark and the applied mode. With the applied mode <c>on</c>, a changed kind queues the
    /// conversation's people runs again (<see cref="RequeuePeopleRunsAsync"/>). False when the segment does not exist.
    /// </summary>
    public async Task<bool> MarkAsync(long segmentId, string? kind, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var rows = (await connection.QueryAsync<(Guid ConversationId, bool Changed)>(new CommandDefinition(
            $"""
            with before as (select id, speech_kind from segments where id = @segmentId),
            upd as (
                update segments s set speech_manual = @kind, speech_kind = {MarkRule} where s.id = @segmentId
                returning s.id, s.conversation_id, s.speech_kind)
            select u.conversation_id as ConversationId, b.speech_kind is distinct from u.speech_kind as Changed
            from upd u join before b on b.id = u.id
            """,
            new { segmentId, kind }, transaction, cancellationToken: ct))).ToList();
        await RequeueChangedAsync(connection, transaction, rows, ct);
        await transaction.CommitAsync(ct);
        return rows.Count > 0;
    }

    /// <summary>
    /// Marks every segment of the conversation that <see cref="SpeakerLabel.IsUser"/> does not make the wearer's (one whose label
    /// is unknown counts), as <see cref="MarkAsync"/> does. Returns how many segments it set.
    /// </summary>
    public async Task<int> MarkConversationAsync(Guid conversationId, string? kind, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var rows = (await connection.QueryAsync<(Guid ConversationId, bool Changed)>(new CommandDefinition(
            $"""
            with before as (select id, speech_kind from segments where conversation_id = @conversationId),
            upd as (
                update segments s set speech_manual = @kind, speech_kind = {MarkRule}
                where s.conversation_id = @conversationId and {SpeakerLabel.IsUser} is not true
                returning s.id, s.conversation_id, s.speech_kind)
            select u.conversation_id as ConversationId, b.speech_kind is distinct from u.speech_kind as Changed
            from upd u join before b on b.id = u.id
            """,
            new { conversationId, kind }, transaction, cancellationToken: ct))).ToList();
        await RequeueChangedAsync(connection, transaction, rows, ct);
        await transaction.CommitAsync(ct);
        return rows.Count;
    }

    /// <summary>
    /// Marks every segment of a reviewed stretch as <see cref="MarkAsync"/> does, in one transaction. False, with nothing changed,
    /// when any of them already carries a mark (or is gone).
    /// </summary>
    public async Task<bool> MarkStretchAsync(long[] segmentIds, string kind, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var set = await connection.ExecuteAsync(new CommandDefinition(
            $"update segments s set speech_manual = @kind, speech_kind = {MarkRule} where s.id = any(@segmentIds) and s.speech_manual is null",
            new { segmentIds, kind }, transaction, cancellationToken: ct));
        if (set != segmentIds.Length)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>A class, not a record: Dapper cannot match a constructor to a <c>text[]</c> column.</summary>
    public sealed class EvalRow
    {
        public long SegmentId { get; set; }

        public Guid ConversationId { get; set; }

        public DateTime StartedAt { get; set; }

        public int DurationMs { get; set; }

        public bool? IsUser { get; set; }

        public string? Guess { get; set; }

        public float? Score { get; set; }

        public string[]? Signals { get; set; }

        public bool Marked { get; set; }

        public string? Kind { get; set; }
    }

    /// <summary>
    /// For the evaluation: every segment with a guess or a mark, oldest first, started after <paramref name="since"/> and before
    /// <paramref name="until"/>. No text.
    /// </summary>
    public async Task<IReadOnlyList<EvalRow>> EvalAsync(DateTimeOffset? since, DateTimeOffset? until, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<EvalRow>(new CommandDefinition(
            $"""
            select s.id as SegmentId, s.conversation_id as ConversationId, s.started_at as StartedAt,
                   (extract(epoch from s.ended_at - s.started_at) * 1000)::int as DurationMs,
                   {SpeakerLabel.IsUser} as IsUser, s.speech_guess as Guess, s.speech_score as Score, s.speech_signals as Signals,
                   s.speech_manual is not null as Marked, s.speech_kind as Kind
            from segments s
            where (s.speech_guess is not null or s.speech_manual is not null)
              and (cast(@since as timestamptz) is null or s.started_at > cast(@since as timestamptz))
              and (cast(@until as timestamptz) is null or s.started_at < cast(@until as timestamptz))
            order by s.started_at, s.id
            limit @limit
            """,
            new { since, until, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Derives every guess again from its stored score at <paramref name="threshold"/>, every kind from the mark and the guess
    /// under <paramref name="mode"/>, and records both as applied, in one transaction. No audio or model is read. With the mode
    /// <c>on</c>, the conversations whose kinds changed queue their people runs again.
    /// </summary>
    public async Task ApplyAsync(string mode, float threshold, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            $"update segments s set speech_guess = {SpeechKinds.Guess} where s.speech_guess is distinct from ({SpeechKinds.Guess})",
            new { threshold }, transaction, cancellationToken: ct));
        var changed = (await connection.QueryAsync<Guid>(new CommandDefinition(
            $"update segments s set speech_kind = {KindRule} where s.speech_kind is distinct from ({KindRule}) returning s.conversation_id",
            new { mode }, transaction, cancellationToken: ct))).Distinct().ToList();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into speech_state (id, applied_mode, applied_threshold, updated_at) values (1, @mode, @threshold, now())
            on conflict (id) do update set
                applied_mode = excluded.applied_mode, applied_threshold = excluded.applied_threshold, updated_at = excluded.updated_at
            """,
            new { mode, threshold }, transaction, cancellationToken: ct));
        if (mode == SpeechKinds.On)
        {
            await RequeuePeopleRunsAsync(connection, transaction, changed, ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static async Task RequeueChangedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<(Guid ConversationId, bool Changed)> rows, CancellationToken ct)
    {
        var changed = rows.Where(r => r.Changed).Select(r => r.ConversationId).Distinct().ToList();
        if (changed.Count > 0 && await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                $"select {SpeechKinds.AppliedMode} = 'on'", transaction: transaction, cancellationToken: ct)))
        {
            await RequeuePeopleRunsAsync(connection, transaction, changed, ct);
        }
    }

    /// <summary>Queues the people runs of the conversations again, as <see cref="RequeuePeopleRunsAsync(NpgsqlConnection, NpgsqlTransaction, IReadOnlyCollection{Guid}, CancellationToken)"/> says.</summary>
    public async Task RequeuePeopleRunsAsync(IReadOnlyCollection<Guid> conversationIds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await RequeuePeopleRunsAsync(connection, transaction, conversationIds, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Makes the people features read these conversations again after their kinds changed, which a changed kind does not do by itself
    /// (runs follow segment ids): a finished summary forgets the segment it read up to (the scan summarizes it again, and a stored
    /// summary queues the names, facts and memories runs), and the conversation's names, facts and memories runs forget theirs and
    /// are marked pending, as a merge does for the memories run. Failures start again.
    /// </summary>
    public static async Task RequeuePeopleRunsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyCollection<Guid> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
        {
            return;
        }

        var args = new { ids = conversationIds.ToArray() };
        await connection.ExecuteAsync(new CommandDefinition(
            "update conversations set ai_through_segment_id = null, ai_failures = 0 where id = any(@ids) and ai_status = 'done'",
            args, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "update people_runs set status = 'pending', through_segment_id = null, failures = 0, message = null where conversation_id = any(@ids)",
            args, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "update memory_runs set status = 'pending', through_segment_id = null, failures = 0, message = null where conversation_id = any(@ids)",
            args, transaction, cancellationToken: ct));
    }

    /// <summary>Whether the stored guesses and kinds follow another mode or threshold than the given ones.</summary>
    public async Task<bool> NeedsApplyAsync(string mode, float threshold, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select not exists (select 1 from speech_state where id = 1 and applied_mode = @mode and applied_threshold = @threshold)",
            new { mode, threshold }, cancellationToken: ct));
    }

    /// <summary>The lines of a conversation in order of start, each with whether <see cref="SpeakerLabel.IsUser"/> makes it the wearer's.</summary>
    public async Task<IReadOnlyList<SpeechLine>> LinesAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<SpeechLine>(new CommandDefinition(
            $"""
            select s.id as Id, s.started_at as StartedAt, s.ended_at as EndedAt, ({SpeakerLabel.IsUser}) as IsUser
            from segments s where s.conversation_id = @conversationId order by s.started_at, s.id
            """,
            new { conversationId }, cancellationToken: ct))).AsList();
    }

    /// <summary>
    /// Stores the classifier's score, signals and <paramref name="version"/> on the lines, then derives their guess at
    /// <paramref name="threshold"/> and the conversation's kinds under <paramref name="mode"/>, in one transaction, as
    /// <see cref="ApplyAsync"/> does for every line. Nothing else a line holds changes, the owner's mark included.
    /// </summary>
    public async Task SaveGuessesAsync(
        Guid conversationId, IReadOnlyList<LineGuess> guesses, short version, string mode, float threshold, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // The guess is a placeholder here: a call stays a call and any other follows its score in the next statement.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update segments s set speech_guess = case when v.call then 'call' else 'person' end, speech_score = v.score,
                speech_signals = string_to_array(v.signals, ','), speech_version = @version
            from unnest(@ids, @calls, @scores, @signals) as v(id, call, score, signals)
            where s.id = v.id and s.conversation_id = @conversationId
            """,
            new
            {
                conversationId,
                version,
                ids = guesses.Select(g => g.SegmentId).ToArray(),
                calls = guesses.Select(g => g.Call).ToArray(),
                scores = guesses.Select(g => g.Score).ToArray(),
                signals = guesses.Select(g => string.Join(',', g.Signals)).ToArray(),
            },
            transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            update segments s set speech_guess = {SpeechKinds.Guess}
            where s.conversation_id = @conversationId and s.speech_guess is distinct from ({SpeechKinds.Guess})
            """,
            new { conversationId, threshold }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            update segments s set speech_kind = {KindRule}
            where s.conversation_id = @conversationId and s.speech_kind is distinct from ({KindRule})
            """,
            new { conversationId, mode }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The closed conversations whose transcription is done (no pending batch) that hold a line the wearer's verdict is known for
    /// and a line with no guess of <paramref name="version"/> or later (with <paramref name="newOnly"/>, no guess of any version),
    /// newest first, at most <paramref name="limit"/>. A conversation with no verdict is left out: its structure says nothing, so
    /// it is not guessed.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> UnguessedAsync(short version, bool newOnly, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            $"select c.id {Unguessed(newOnly)} order by c.ended_at desc limit @limit", new { version, limit }, cancellationToken: ct))).AsList();
    }

    /// <summary>How many conversations <see cref="UnguessedAsync"/> would list without its limit.</summary>
    public async Task<int> CountUnguessedAsync(short version, bool newOnly, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(*)::int {Unguessed(newOnly)}", new { version }, cancellationToken: ct));
    }

    private static string Unguessed(bool newOnly) =>
        $"""
        from conversations c
        where c.status = 'closed'
          and not exists (select 1 from transcription_batches b where b.conversation_id = c.id and b.status = 'pending')
          and exists (
              select 1 from segments s where s.conversation_id = c.id
                and {(newOnly ? "s.speech_version is null" : "(s.speech_version is null or s.speech_version < @version)")})
          and exists (select 1 from segments s where s.conversation_id = c.id and ({SpeakerLabel.IsUser}) is not null)
        """;
}
