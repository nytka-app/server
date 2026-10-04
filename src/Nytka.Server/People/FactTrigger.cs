using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>
/// Queues <c>extract-person-facts</c> for every stored summary (<c>conversation.ready</c>), in the summary's own
/// transaction, when <c>people.facts</c> is on, the model is configured, the conversation is not brief and it has
/// someone to write facts about.
/// </summary>
public sealed class FactTrigger(
    IServiceProvider services, SettingsService settings, PersonFactStore facts, JobQueue queue, TimeProvider time)
    : IEventSubscriber
{
    public async Task OnEventAsync(
        NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (nytkaEvent.Type != NytkaEvent.ConversationReady
            || !PeopleSettings.FactsEnabled(settings)
            || !services.GetRequiredService<ILlmClient>().IsConfigured
            || await facts.ReadInputAsync(connection, transaction, nytkaEvent.SubjectId, ct) is not { } input
            || EnrichConversationHandler.IsBrief(input.Segments.Sum(s => EnrichConversationHandler.Words(s.Text)))
            || FactBasis.Involved(input.Segments, await facts.PeopleAsync(connection, transaction, ct)).Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (!await facts.MarkPendingAsync(connection, transaction, nytkaEvent.SubjectId, now, ct))
        {
            return;
        }

        await queue.EnqueueAsync(
            connection, transaction, JobKinds.ExtractPersonFacts, new ExtractPayload(nytkaEvent.SubjectId),
            JobKinds.ExtractPersonFactsKey(nytkaEvent.SubjectId), now, ct);
    }
}
