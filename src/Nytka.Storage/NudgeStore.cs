using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A nudge as the app gets it. <see cref="At"/> is when it may be shown.</summary>
public sealed record Nudge(string Id, string Kind, string Title, string Body, string DeepLink, DateTime At);

/// <summary>A nudge before it is stored; <see cref="SubjectId"/> is the task of a <c>commitment-due</c> nudge.</summary>
public sealed record NudgeCandidate(string Id, string Kind, Guid? SubjectId, string Title, string Body, string DeepLink, DateTime At);

/// <summary>An open commitment due within the window, with the conversation it was said in.</summary>
public sealed record DueCommitment(Guid Id, string Text, DateTime Due, Guid ConversationId);

/// <summary>Nudges (<c>nudges</c>): made once, read by <c>GET /api/v1/nudges</c>.</summary>
public sealed class NudgeStore(NpgsqlDataSource dataSource)
{
    /// <summary>Open commitments due after <paramref name="now"/> and by <paramref name="until"/>; a hidden task or conversation never shows.</summary>
    public async Task<IReadOnlyList<DueCommitment>> DueCommitmentsAsync(DateTime now, DateTime until, string[] hidden, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<DueCommitment>(new CommandDefinition(
            $"""
            select t.id as Id, t.text as Text, t.due_at as Due, t.conversation_id as ConversationId
            from tasks t join conversations c on c.id = t.conversation_id
            where t.kind = @kind and not t.done and t.deleted_at is null and t.due_at > @now and t.due_at <= @until
              and {Sensitivity.Visible("t")} and {Sensitivity.Visible("c")}
            order by t.due_at, t.id
            """,
            new { kind = TaskKinds.Commitment, now, until, hidden }, cancellationToken: ct))).ToList();
    }

    /// <summary>Stores the candidates that are new; one that exists keeps its row.</summary>
    public async Task SaveAsync(IReadOnlyList<NudgeCandidate> candidates, DateTimeOffset now, CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var c in candidates)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into nudges (id, kind, subject_id, title, body, deep_link, at, created_at)
                values (@Id, @Kind, @SubjectId, @Title, @Body, @DeepLink, @At, @now)
                on conflict (id) do nothing
                """,
                new { c.Id, c.Kind, c.SubjectId, c.Title, c.Body, c.DeepLink, c.At, now = now.UtcDateTime }, transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The nudges shown from <paramref name="since"/> up to <paramref name="now"/>, oldest first. A <c>commitment-due</c> nudge is
    /// dropped once its task is done, deleted, or hidden, or its conversation is hidden.
    /// </summary>
    public async Task<IReadOnlyList<Nudge>> ListAsync(DateTime since, DateTime now, string[] hidden, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<Nudge>(new CommandDefinition(
            $"""
            select n.id as Id, n.kind as Kind, n.title as Title, n.body as Body, n.deep_link as DeepLink, n.at as At
            from nudges n
            left join tasks t on t.id = n.subject_id
            left join conversations c on c.id = t.conversation_id
            where n.at >= @since and n.at <= @now
              and (n.kind <> 'commitment-due'
                   or (t.id is not null and not t.done and t.deleted_at is null and {Sensitivity.Visible("t")} and {Sensitivity.Visible("c")}))
            order by n.at, n.id
            """,
            new { since, now, hidden }, cancellationToken: ct))).ToList();
    }
}
