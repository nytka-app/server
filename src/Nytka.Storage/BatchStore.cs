using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed record NewSpeechAudio(DateTimeOffset StartedAt, DateTimeOffset EndedAt, byte[] Body);

public sealed record NewBatch(
    Guid ConversationId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, byte[] Wav, string OffsetMapJson,
    IReadOnlyList<NewSpeechAudio> SpeechAudio);

public sealed record PendingBatch(long Id, Guid ConversationId, DateTime StartedAt, DateTime EndedAt, byte[] Wav, string OffsetMap);

public sealed record NewSegment(
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Text, string? Speaker = null, string? SpeakerId = null, bool? IsUser = null);

/// <summary>Where transcription stands. <paramref name="LastError"/> is set only while it is current: no batch has finished since.</summary>
public sealed record BatchOutcomes(string? LastError, DateTime? LastErrorAt, DateTime? LastSuccessAt);

public sealed record BatchRow(long Id, DateTime StartedAt, DateTime EndedAt, string Status, string? Error, string? Response);

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
            set status = 'done', wav = null, error = null, response = cast(@response as jsonb), finished_at = now()
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
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker, speaker_id, is_user)
            values (@ConversationId, @BatchId, @StartedAt, @EndedAt, @Text, @Speaker, @SpeakerId, @IsUser)
            """,
            segments.Select(s => new { ConversationId = conversationId.Value, BatchId = id, s.StartedAt, s.EndedAt, s.Text, s.Speaker, s.SpeakerId, s.IsUser }),
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
            "update transcription_batches set status = 'failed', error = @error, finished_at = now() where id = @id and status = 'pending'",
            new { id, error }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<BatchRow>> ListForConversationAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<BatchRow>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, ended_at as EndedAt, status as Status, error as Error,
                   response::text as Response
            from transcription_batches where conversation_id = @conversationId
            order by started_at, id
            """,
            new { conversationId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<bool> HasPendingAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from transcription_batches where conversation_id = @conversationId and status = 'pending')",
            new { conversationId }, cancellationToken: ct));
    }

    /// <summary>
    /// When the newest failed and the newest done batch finished, and the newest failure's error
    /// while it is current. A failure is current until a batch finishes <c>done</c> after it; batches
    /// from before <c>finished_at</c> existed have no time, so any finished batch is later than them.
    /// </summary>
    public async Task<BatchOutcomes> OutcomesAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleAsync<BatchOutcomes>(new CommandDefinition(
            """
            select case when s.at is null or f.at > s.at then f.error end as LastError,
                   f.at as LastErrorAt, s.at as LastSuccessAt
            from (select 1) one
            left join lateral (
                select error, finished_at as at from transcription_batches where status = 'failed'
                order by finished_at desc nulls last, created_at desc, id desc limit 1) f on true
            left join lateral (
                select max(finished_at) as at from transcription_batches where status = 'done') s on true
            """,
            cancellationToken: ct));
    }

    /// <summary>A conversation's speech audio bodies (chunk format) in capture order.</summary>
    public async Task<IReadOnlyList<byte[]>> SpeechAudioBodiesAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<byte[]>(new CommandDefinition(
            "select body from speech_audio where conversation_id = @conversationId order by started_at, id",
            new { conversationId }, cancellationToken: ct))).AsList();
    }

    public async Task<int> DeleteSpeechAudioEndedBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from speech_audio
            where ended_at < @before
              and batch_id in (select id from transcription_batches where status = 'done')
            """,
            new { before }, cancellationToken: ct));
    }
}
