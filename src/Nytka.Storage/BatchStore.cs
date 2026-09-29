using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed record NewSpeechAudio(DateTimeOffset StartedAt, DateTimeOffset EndedAt, byte[] Body);

public sealed record NewBatch(
    Guid ConversationId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, byte[] Wav, string OffsetMapJson,
    IReadOnlyList<NewSpeechAudio> SpeechAudio);

public sealed class BatchStore
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
}
