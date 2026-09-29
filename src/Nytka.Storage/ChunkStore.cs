using Dapper;
using Npgsql;
using Nytka.Audio.Frames;

namespace Nytka.Storage;

public enum StoreOutcome
{
    Stored,
    Duplicate,
    Overlap,
}

public sealed class ChunkStore(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Stores a chunk once. A chunk with the same session, first sequence number and frame count
    /// is a duplicate, even after its audio was processed (the row outlives its body).
    /// </summary>
    public async Task<StoreOutcome> StoreAsync(Chunk chunk, byte[] body, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Uploads for one session run one at a time.
        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(hashtextextended(@session::text, 0))",
            new { session = chunk.Session }, transaction, cancellationToken: ct));

        var key = new { session = chunk.Session, firstSeq = (long)chunk.FirstSeq, lastSeq = (long)chunk.LastSeq };

        var existing = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            "select frame_count from audio_chunks where session_id = @session and first_seq = @firstSeq",
            key, transaction, cancellationToken: ct));
        if (existing is not null)
        {
            return existing == chunk.Frames.Count ? StoreOutcome.Duplicate : StoreOutcome.Overlap;
        }

        var overlaps = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1 from audio_chunks
                where session_id = @session
                  and first_seq <= @lastSeq
                  and first_seq + frame_count - 1 >= @firstSeq)
            """,
            key, transaction, cancellationToken: ct));
        if (overlaps)
        {
            return StoreOutcome.Overlap;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into capture_sessions (id, started_at, last_received_at)
            values (@session, @startedAt, @receivedAt)
            on conflict (id) do update set last_received_at = excluded.last_received_at
            """,
            new { session = chunk.Session, startedAt = DateTimeOffset.FromUnixTimeMilliseconds(chunk.BaseTimeMs), receivedAt },
            transaction, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into audio_chunks (session_id, first_seq, frame_count, base_time, last_time, body, received_at)
            values (@session, @firstSeq, @frameCount, @baseTime, @lastTime, @body, @receivedAt)
            """,
            new
            {
                session = chunk.Session,
                firstSeq = (long)chunk.FirstSeq,
                frameCount = chunk.Frames.Count,
                baseTime = DateTimeOffset.FromUnixTimeMilliseconds(chunk.BaseTimeMs),
                lastTime = DateTimeOffset.FromUnixTimeMilliseconds(chunk.Frames[^1].EndMs),
                body,
                receivedAt,
            },
            transaction, cancellationToken: ct));

        await transaction.CommitAsync(ct);
        return StoreOutcome.Stored;
    }
}
