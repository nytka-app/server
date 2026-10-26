using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary><paramref name="Pending"/>: the conversation holds pending model suggestions of names.</summary>
public sealed record BackfillCandidate(Guid Id, DateTime StartedAt, bool Pending);

/// <summary>What the People backfill reads: summarized conversations with no completed run of a kind, and the jobs already queued.</summary>
public sealed class PeopleBackfillStore
{
    /// <summary>
    /// Conversations with a finished summary and speech that have no <c>done</c> row in <c>people_runs</c> for
    /// <paramref name="runKind"/> (<c>names</c> or <c>facts</c>), newest first. Pending and failed runs count as not completed,
    /// and so does a done run made under a validator version below <paramref name="minValidator"/>. With
    /// <paramref name="withPending"/>, so does any run of a conversation that still holds pending model suggestions of names.
    /// </summary>
    public async Task<IReadOnlyList<BackfillCandidate>> EligibleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string runKind, int minValidator, CancellationToken ct,
        bool withPending = false) =>
        (await connection.QueryAsync<BackfillCandidate>(new CommandDefinition(
            """
            select c.id as Id, c.started_at as StartedAt,
                   @withPending and exists (
                       select 1 from name_suggestions n
                       where n.conversation_id = c.id and n.status = 'pending' and n.target in ('speaker', 'label', 'person')) as Pending
            from conversations c
            where c.ai_status = 'done'
              and exists (select 1 from segments s where s.conversation_id = c.id)
              and (not exists (
                       select 1 from people_runs r where r.conversation_id = c.id and r.kind = @runKind and r.status = 'done'
                         and r.validator >= @minValidator)
                   or @withPending and exists (
                       select 1 from name_suggestions n
                       where n.conversation_id = c.id and n.status = 'pending' and n.target in ('speaker', 'label', 'person')))
            order by c.started_at desc, c.id desc
            """,
            new { runKind, minValidator, withPending }, transaction, cancellationToken: ct))).ToList();

    /// <summary>The dedupe keys of the waiting or running jobs of a kind.</summary>
    public async Task<IReadOnlySet<string>> QueuedKeysAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string jobKind, CancellationToken ct) =>
        (await connection.QueryAsync<string>(new CommandDefinition(
            "select dedupe_key from jobs where kind = @jobKind and dedupe_key is not null",
            new { jobKind }, transaction, cancellationToken: ct))).ToHashSet();
}
