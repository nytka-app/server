using Dapper;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.DayEvents;

/// <summary>Queues <c>recompute-events</c> for a conversation's local date after <c>conversation.ready</c>, in the same transaction.</summary>
public sealed class RecomputeEventsTrigger(JobQueue queue, SettingsService settings, TimeProvider time) : IEventSubscriber
{
    public async Task OnEventAsync(NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (nytkaEvent.Type != NytkaEvent.ConversationReady)
        {
            return;
        }

        var startedAt = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "select started_at from conversations where id = @id", new { id = nytkaEvent.SubjectId }, transaction, cancellationToken: ct));
        if (startedAt is not { } at)
        {
            return;
        }

        var now = time.GetUtcNow();
        var zone = UserTimeZone.Resolve(settings);
        var date = DigestDay.Text(DigestDay.Today(new DateTimeOffset(at, TimeSpan.Zero), zone));
        await queue.EnqueueAsync(
            connection, transaction, JobKinds.RecomputeEvents, new DayEventsPayload(date), JobKinds.RecomputeEventsKey(date), now, ct);
    }
}
