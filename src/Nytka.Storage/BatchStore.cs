using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed record NewSpeechAudio(DateTimeOffset StartedAt, DateTimeOffset EndedAt, byte[] Body);

public sealed record NewBatch(
    Guid ConversationId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, byte[] Wav, string OffsetMapJson,
    IReadOnlyList<NewSpeechAudio> SpeechAudio);

public sealed record PendingBatch(long Id, Guid ConversationId, DateTime StartedAt, DateTime EndedAt, byte[] Wav, string OffsetMap);

public sealed record NewSegment(DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Text);

public sealed class BatchStore(NpgsqlDataSource dataSource)
{
    /// <summary>Inserts a pending batch and its speech audio inside the caller's transaction.</summary>
    public async Task<long> CreateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NewBatch batch, DateTimeOffset now, CancellationToken ct)
    {
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, wav, offset_map, created_at)
            values (@ConversationId, @StartedAt, @EndedAt, 'pending', @Wav, cast(@OffsetMapJson as jsonb), @now)
            returning id
            """,
            new { batch.ConversationId, batch.StartedAt, batch.EndedAt, batch.Wav, batch.OffsetMapJson, now },
            transaction, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into speech_audio (conversation_id, batch_id, started_at, ended_at, body)
            values (@ConversationId, @BatchId, @StartedAt, @EndedAt, @Body)
            """,
            batch.SpeechAudio.Select(a => new { batch.ConversationId, BatchId = id, a.StartedAt, a.EndedAt, a.Body }),
            transaction, cancellationToken: ct));

        return id;
    }

    public async Task<PendingBatch?> GetPendingAsync(long id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PendingBatch>(new CommandDefinition(
            """
            select id as Id, conversation_id as ConversationId, started_at as StartedAt, ended_at as EndedAt,
                   wav as Wav, offset_map::text as OffsetMap
            from transcription_batches
            where id = @id and status = 'pending'
            """,
            new { id }, cancellationToken: ct));
    }

    /// <summary>
    /// Marks the batch done with its segments and raw response and drops the WAV. Does nothing when
    /// the batch is no longer pending (deleted with its conversation, or already finished).
    /// </summary>
    public async Task CompleteAsync(
        long id, string response, IReadOnlyList<NewSegment> segments, bool deleteSpeechAudio, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var conversationId = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            update transcription_batches
            set status = 'done', wav = null, error = null, response = cast(@response as jsonb)
            where id = @id and status = 'pending'
            returning conversation_id
            """,
            new { id, response }, transaction, cancellationToken: ct));
        if (conversationId is null)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text)
            values (@ConversationId, @BatchId, @StartedAt, @EndedAt, @Text)
            """,
            segments.Select(s => new { ConversationId = conversationId.Value, BatchId = id, s.StartedAt, s.EndedAt, s.Text }),
            transaction, cancellationToken: ct));

        if (deleteSpeechAudio)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from speech_audio where batch_id = @id", new { id }, transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }

    public async Task FailAsync(long id, string error, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "update transcription_batches set status = 'failed', error = @error where id = @id and status = 'pending'",
            new { id, error }, cancellationToken: ct));
    }
}
