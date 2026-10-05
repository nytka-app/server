using Dapper;
using Npgsql;

namespace Nytka.Storage;

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
}
