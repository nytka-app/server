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
}
