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
    /// kind in the same statement from the mark and the applied mode. False when the segment does not exist.
    /// </summary>
    public async Task<bool> MarkAsync(long segmentId, string? kind, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            $"update segments s set speech_manual = @kind, speech_kind = {MarkRule} where s.id = @segmentId",
            new { segmentId, kind }, cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Marks every segment of the conversation that <see cref="SpeakerLabel.IsUser"/> does not make the wearer's (one whose label
    /// is unknown counts), as <see cref="MarkAsync"/> does. Returns how many segments it set.
    /// </summary>
    public async Task<int> MarkConversationAsync(Guid conversationId, string? kind, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            $"""
            update segments s set speech_manual = @kind, speech_kind = {MarkRule}
            where s.conversation_id = @conversationId and {SpeakerLabel.IsUser} is not true
            """,
            new { conversationId, kind }, cancellationToken: ct));
    }

    /// <summary>
    /// Derives every guess again from its stored score at <paramref name="threshold"/>, every kind from the mark and the guess
    /// under <paramref name="mode"/>, and records both as applied, in one transaction. No audio or model is read.
    /// </summary>
    public async Task ApplyAsync(string mode, float threshold, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            $"update segments s set speech_guess = {SpeechKinds.Guess} where s.speech_guess is distinct from ({SpeechKinds.Guess})",
            new { threshold }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            $"update segments s set speech_kind = {KindRule} where s.speech_kind is distinct from ({KindRule})",
            new { mode }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into speech_state (id, applied_mode, applied_threshold, updated_at) values (1, @mode, @threshold, now())
            on conflict (id) do update set
                applied_mode = excluded.applied_mode, applied_threshold = excluded.applied_threshold, updated_at = excluded.updated_at
            """,
            new { mode, threshold }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
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
