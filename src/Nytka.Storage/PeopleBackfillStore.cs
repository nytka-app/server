using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed record BackfillCandidate(Guid Id, DateTime StartedAt);

/// <summary>What the People backfill reads: summarized conversations with no completed run of a kind, and the jobs already queued.</summary>
public sealed class PeopleBackfillStore
{
    /// <summary>
    /// Conversations with a finished summary and speech that have no <c>done</c> row in <c>people_runs</c> for
    /// <paramref name="runKind"/> (<c>names</c> or <c>facts</c>), newest first. Pending and failed runs count as not completed.
    /// </summary>
    public async Task<IReadOnlyList<BackfillCandidate>> EligibleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string runKind, CancellationToken ct) =>
        (await connection.QueryAsync<BackfillCandidate>(new CommandDefinition(
            """
            select c.id as Id, c.started_at as StartedAt
            from conversations c
            where c.ai_status = 'done'
              and exists (select 1 from segments s where s.conversation_id = c.id)
              and not exists (
                  select 1 from people_runs r where r.conversation_id = c.id and r.kind = @runKind and r.status = 'done')
            order by c.started_at desc, c.id desc
            """,
            new { runKind }, transaction, cancellationToken: ct))).ToList();

    /// <summary>The dedupe keys of the waiting or running jobs of a kind.</summary>
    public async Task<IReadOnlySet<string>> QueuedKeysAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string jobKind, CancellationToken ct) =>
        (await connection.QueryAsync<string>(new CommandDefinition(
            "select dedupe_key from jobs where kind = @jobKind and dedupe_key is not null",
            new { jobKind }, transaction, cancellationToken: ct))).ToHashSet();
}
