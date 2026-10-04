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

/// <summary>
/// <see cref="ProcessedEndSeq"/> is one past the last frame of the processed chunks (null when none
/// is left): where the next chunk should start.
/// </summary>
public sealed record SessionState(DateTime? ProcessedThroughAt, DateTime LastReceivedAt, long? ProcessedEndSeq);

public sealed record PendingChunk(long FirstSeq, int FrameCount, DateTime ReceivedAt, byte[] Body);

/// <summary>A session that holds chunk audio, and whether any of it was late when it arrived.</summary>
public sealed record PendingSession(Guid Id, bool Late);

/// <summary>What a chunk row keeps once its audio is processed: where its frames sit in the session and in time.</summary>
public sealed record ChunkSpan(Guid Session, long FirstSeq, int FrameCount, DateTime BaseTime, DateTime LastTime);

public sealed record PendingSummary(long PendingChunks, DateTime? OldestPendingAt);

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

    /// <summary>Sessions that still hold chunk audio the pipeline has not finished with.</summary>
    public async Task<IReadOnlyList<Guid>> SessionsWithPendingAudioAsync(CancellationToken ct) =>
        [.. (await PendingSessionsAsync(ct)).Select(s => s.Id)];

    /// <summary>
    /// Like <see cref="SessionsWithPendingAudioAsync"/>, with each session's priority. A session is late
    /// when its newest waiting chunk was (last frame over <see cref="JobPriority.LateAfter"/> old when
    /// it arrived, as <c>ChunkEndpoints</c> judges it), so live audio after a backlog is not held behind it.
    /// </summary>
    public async Task<IReadOnlyList<PendingSession>> PendingSessionsAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var sessions = await connection.QueryAsync<PendingSession>(new CommandDefinition(
            """
            select distinct on (session_id) session_id as Id, received_at - last_time > @lateAfter as Late
            from audio_chunks where body is not null
            order by session_id, first_seq desc
            """,
            new { lateAfter = JobPriority.LateAfter }, cancellationToken: ct));
        return sessions.ToList();
    }

    public async Task<SessionState?> GetSessionAsync(Guid session, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<SessionState>(new CommandDefinition(
            """
            select s.processed_through_at as ProcessedThroughAt,
                   s.last_received_at     as LastReceivedAt,
                   (select max(c.first_seq + c.frame_count) from audio_chunks c
                    where c.session_id = s.id and c.body is null) as ProcessedEndSeq
            from capture_sessions s
            where s.id = @session
            """,
            new { session }, cancellationToken: ct));
    }

    /// <summary>Chunks that still hold audio, in sequence order.</summary>
    public async Task<IReadOnlyList<PendingChunk>> LoadPendingAsync(Guid session, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var chunks = await connection.QueryAsync<PendingChunk>(new CommandDefinition(
            """
            select first_seq as FirstSeq, frame_count as FrameCount, received_at as ReceivedAt, body as Body
            from audio_chunks
            where session_id = @session and body is not null
            order by first_seq
            limit @limit
            """,
            new { session, limit }, cancellationToken: ct));
        return chunks.ToList();
    }

    /// <summary>
    /// Moves the session's processed point and drops the audio of used chunks that lie wholly
    /// before it. A chunk that reaches past the point keeps its body for the next run.
    /// </summary>
    public async Task MarkProcessedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid session, DateTimeOffset processedThrough,
        long[] usedFirstSeqs, DateTimeOffset now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "update capture_sessions set processed_through_at = @processedThrough where id = @session",
            new { session, processedThrough }, transaction, cancellationToken: ct));

        await connection.ExecuteAsync(new CommandDefinition(
            """
            update audio_chunks set body = null, processed_at = @now
            where session_id = @session and body is not null
              and first_seq = any(@usedFirstSeqs) and last_time <= @processedThrough
            """,
            new { session, processedThrough, usedFirstSeqs, now }, transaction, cancellationToken: ct));
    }

    /// <summary>True when chunks with audio arrived that the caller has not seen.</summary>
    public async Task<bool> HasPendingBeyondAsync(Guid session, long[] seenFirstSeqs, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            select exists (
                select 1 from audio_chunks
                where session_id = @session and body is not null and not (first_seq = any(@seenFirstSeqs)))
            """,
            new { session, seenFirstSeqs }, cancellationToken: ct));
    }

    /// <summary>Chunks whose audio the pipeline has not finished with, and when the oldest arrived.</summary>
    public async Task<PendingSummary> PendingAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleAsync<PendingSummary>(new CommandDefinition(
            """
            select count(*) as PendingChunks, min(received_at) as OldestPendingAt
            from audio_chunks where body is not null
            """,
            cancellationToken: ct));
    }

    /// <summary>
    /// Every chunk row of the sessions that have audio between <paramref name="from"/> and <paramref name="to"/>
    /// or are named in <paramref name="sessions"/>, so a hole between two chunks shows even when one lies outside.
    /// </summary>
    public async Task<IReadOnlyList<ChunkSpan>> ListSpansAsync(
        DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<Guid> sessions, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ChunkSpan>(new CommandDefinition(
            """
            select session_id as Session, first_seq as FirstSeq, frame_count as FrameCount, base_time as BaseTime, last_time as LastTime
            from audio_chunks
            where session_id = any(@sessions)
               or session_id in (select session_id from audio_chunks where last_time >= @from and base_time < @to)
            order by session_id, first_seq
            """,
            new { from, to, sessions = sessions.ToArray() }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Deletes rows of processed chunks (no body) received before <paramref name="before"/>.</summary>
    public async Task<int> DeleteProcessedReceivedBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from audio_chunks where body is null and received_at < @before", new { before }, cancellationToken: ct));
    }
}
