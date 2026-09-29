using System.Text.Json;
using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>Which kinds of job a consumer takes: only the listed ones, or every kind but them.</summary>
public readonly record struct JobKindFilter(IReadOnlyCollection<string> Kinds, bool Exclude)
{
    public static JobKindFilter Any => new([], true);

    public static JobKindFilter Only(IEnumerable<string> kinds) => new([.. kinds], false);

    public static JobKindFilter Except(IEnumerable<string> kinds) => new([.. kinds], true);
}

/// <summary>
/// Which jobs run first. Each step of priority counts as <see cref="Step"/> of extra waiting: a late
/// job yields to live jobs due within that time of it, and runs ahead of any due later.
/// </summary>
public static class JobPriority
{
    /// <summary>Live audio and every other kind of job.</summary>
    public const short Live = 0;

    /// <summary>Late audio, such as a stored backlog: it yields to live speech.</summary>
    public const short Late = 1;

    /// <summary>The delay one step of priority adds to a job's place in line, so late work cannot starve.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMinutes(10);

    /// <summary>Audio whose last frame is older than this when it reaches the server counts as late.</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(5);

    public static short ForAudioEndingAt(DateTimeOffset lastFrameEnd, DateTimeOffset now) =>
        now - lastFrameEnd > LateAfter ? Late : Live;
}

public sealed class JobQueue(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Queues a job. With a <paramref name="dedupeKey"/>, nothing happens while a job with the
    /// same key is waiting or running, except that it takes the lower of the two priorities.
    /// </summary>
    public async Task EnqueueAsync(
        string kind, object payload, string? dedupeKey, DateTimeOffset runAfter, CancellationToken ct,
        short priority = JobPriority.Live)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await EnqueueAsync(connection, null, kind, payload, dedupeKey, runAfter, ct, priority);
    }

    /// <summary>Queues a job inside the caller's transaction: it exists only if that transaction commits.</summary>
    public Task EnqueueAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string kind, object payload, string? dedupeKey,
        DateTimeOffset runAfter, CancellationToken ct, short priority = JobPriority.Live) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            insert into jobs (kind, payload, dedupe_key, run_after, priority, created_at)
            values (@kind, cast(@payload as jsonb), @dedupeKey, @runAfter, @priority, now())
            on conflict (dedupe_key) where dedupe_key is not null
            do update set priority = least(jobs.priority, excluded.priority)
            """,
            new { kind, payload = JsonSerializer.Serialize(payload), dedupeKey, runAfter, priority },
            transaction, cancellationToken: ct));

    /// <summary>
    /// Leases the next due job of the given kinds until <c>now + lease</c> and counts the attempt. A
    /// job whose lease ran out (the process died mid-job) is due again.
    /// </summary>
    public async Task<JobRecord?> DequeueAsync(DateTimeOffset now, TimeSpan lease, JobKindFilter kinds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<JobRecord>(new CommandDefinition(
            """
            update jobs set locked_until = @lockedUntil, attempts = attempts + 1
            where id = (
                select id from jobs
                where run_after <= @now and (locked_until is null or locked_until < @now)
                  and case when @exclude then kind <> all(@kinds) else kind = any(@kinds) end
                order by run_after + priority * cast(@step as interval), id
                for update skip locked
                limit 1)
            returning id as Id, kind as Kind, payload::text as Payload, attempts as Attempts
            """,
            new { now, lockedUntil = now + lease, kinds = kinds.Kinds.ToArray(), exclude = kinds.Exclude, step = JobPriority.Step },
            cancellationToken: ct));
    }

    public Task CompleteAsync(long id, CancellationToken ct) =>
        ExecuteAsync("delete from jobs where id = @id", new { id }, ct);

    /// <summary>Puts a job back to run at <paramref name="runAfter"/> with fresh attempts.</summary>
    public Task RescheduleAsync(long id, DateTimeOffset runAfter, CancellationToken ct) =>
        ExecuteAsync(
            "update jobs set run_after = @runAfter, locked_until = null, attempts = 0, last_error = null where id = @id",
            new { id, runAfter },
            ct);

    /// <summary>Records a failed attempt; the job runs again at <paramref name="runAfter"/>.</summary>
    public Task FailAsync(long id, string error, DateTimeOffset runAfter, CancellationToken ct) =>
        ExecuteAsync(
            "update jobs set run_after = @runAfter, locked_until = null, last_error = @error where id = @id",
            new { id, error, runAfter },
            ct);

    private async Task ExecuteAsync(string sql, object args, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
    }
}
