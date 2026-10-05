using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>
/// Queues <c>suggest-names</c> for a stored summary (<c>conversation.ready</c>), in the summary's own transaction, when
/// <c>people.suggestNames</c> is on, the model is configured, the conversation is not brief and has an unnamed voice, and
/// it holds segments the last run did not read.
/// </summary>
public sealed class NameSuggestionTrigger(
    IServiceProvider services, SettingsService settings, NameSuggestionStore suggestions, JobQueue queue, TimeProvider time)
    : IEventSubscriber
{
    public async Task OnEventAsync(
        NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (nytkaEvent.Type != NytkaEvent.ConversationReady
            || !PeopleSettings.SuggestNames(settings)
            || !services.GetRequiredService<ILlmClient>().IsConfigured
            || await suggestions.ReadInputAsync(connection, transaction, nytkaEvent.SubjectId, ct) is not { } input
            || EnrichConversationHandler.IsBrief(input.Segments.Sum(s => EnrichConversationHandler.Words(s.Text)))
            || NameTargets.Find(input.Segments).Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (!await suggestions.MarkPendingAsync(connection, transaction, nytkaEvent.SubjectId, NameValidator.Version, now, ct))
        {
            return;
        }

        await queue.EnqueueAsync(
            connection, transaction, JobKinds.SuggestNames, new SuggestNamesPayload(nytkaEvent.SubjectId),
            JobKinds.SuggestNamesKey(nytkaEvent.SubjectId), now, ct);
    }
}
