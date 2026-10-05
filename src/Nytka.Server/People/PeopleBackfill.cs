using Npgsql;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.People;

public sealed record BackfillQueued(int SuggestNames, int Facts);

/// <summary>
/// <c>queued</c>: jobs added now. <c>skipped</c>: eligible runs whose job already waits or runs, or that the last run already
/// covers. <c>remaining</c>: eligible conversations beyond the limit.
/// </summary>
public sealed record BackfillResult(BackfillQueued Queued, int Skipped, int Remaining);

/// <summary>
/// Queues <c>suggest-names</c> and <c>extract-person-facts</c> for conversations summarized before the People features
/// existed (docs/specs/people.md). It does what <see cref="NameSuggestionTrigger"/> and <see cref="FactTrigger"/> do for a
/// stored summary, without the summary: no enrichment runs and no event is published. The jobs queue at late priority, so a
/// backlog never delays the summary of a live conversation.
/// </summary>
public sealed class PeopleBackfill(
    NpgsqlDataSource dataSource, PeopleBackfillStore backfill, NameSuggestionStore names, PersonFactStore facts, JobQueue queue,
    SettingsService settings, TimeProvider time)
{
    public async Task<BackfillResult> RunAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var skipped = 0;
        var todoNames = PeopleSettings.SuggestNames(settings)
            ? await TodoAsync(connection, transaction, NameSuggestionStore.Names, JobKinds.SuggestNames, JobKinds.SuggestNamesKey, ct)
            : ([], 0);
        var todoFacts = PeopleSettings.FactsEnabled(settings)
            ? await TodoAsync(connection, transaction, PersonFactStore.Facts, JobKinds.ExtractPersonFacts, JobKinds.ExtractPersonFactsKey, ct)
            : ([], 0);
        skipped += todoNames.Waiting + todoFacts.Waiting;

        var newestFirst = todoNames.Todo.Concat(todoFacts.Todo).DistinctBy(c => c.Id)
            .OrderByDescending(c => c.StartedAt).ThenByDescending(c => c.Id).ToList();
        var window = newestFirst.Take(limit).Select(c => c.Id).ToHashSet();

        var now = time.GetUtcNow();
        var suggestNames = 0;
        foreach (var id in todoNames.Todo.Where(c => window.Contains(c.Id)).Select(c => c.Id))
        {
            if (!await names.MarkPendingAsync(connection, transaction, id, now, ct))
            {
                skipped++;
                continue;
            }

            await queue.EnqueueAsync(
                connection, transaction, JobKinds.SuggestNames, new SuggestNamesPayload(id), JobKinds.SuggestNamesKey(id), now, ct,
                JobPriority.Late);
            suggestNames++;
        }

        var factJobs = 0;
        foreach (var id in todoFacts.Todo.Where(c => window.Contains(c.Id)).Select(c => c.Id))
        {
            if (!await facts.MarkPendingAsync(connection, transaction, id, now, ct))
            {
                skipped++;
                continue;
            }

            await queue.EnqueueAsync(
                connection, transaction, JobKinds.ExtractPersonFacts, new ExtractPayload(id), JobKinds.ExtractPersonFactsKey(id), now, ct,
                JobPriority.Late);
            factJobs++;
        }

        await transaction.CommitAsync(ct);
        return new BackfillResult(new BackfillQueued(suggestNames, factJobs), skipped, newestFirst.Count - window.Count);
    }

    /// <summary>The eligible conversations of a kind with no job waiting for them, and the number that have one.</summary>
    private async Task<(IReadOnlyList<BackfillCandidate> Todo, int Waiting)> TodoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string runKind, string jobKind, Func<Guid, string> key,
        CancellationToken ct)
    {
        var queued = await backfill.QueuedKeysAsync(connection, transaction, jobKind, ct);
        var eligible = await backfill.EligibleAsync(connection, transaction, runKind, ct);
        var todo = eligible.Where(c => !queued.Contains(key(c.Id))).ToList();
        return (todo, eligible.Count - todo.Count);
    }
}
