using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Memories;

/// <summary>
/// Queues <c>extract-memories</c> for every stored summary (<c>conversation.ready</c>), in the summary's own
/// transaction, when <c>memories.enabled</c> is on and the model is configured. The model client is looked up
/// per event, so a host without one simply extracts nothing.
/// </summary>
public sealed class MemoryTrigger(
    IServiceProvider services, SettingsService settings, MemoryStore memories, JobQueue queue, TimeProvider time)
    : IEventSubscriber
{
    public async Task OnEventAsync(
        NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (nytkaEvent.Type != NytkaEvent.ConversationReady
            || !MemorySettings.IsEnabled(settings)
            || services.GetService(typeof(ILlmClient)) is not ILlmClient { IsConfigured: true })
        {
            return;
        }

        var now = time.GetUtcNow();
        await memories.MarkPendingAsync(connection, transaction, nytkaEvent.SubjectId, now, ct);
        await queue.EnqueueAsync(
            connection, transaction, JobKinds.ExtractMemories, new ExtractPayload(nytkaEvent.SubjectId),
            JobKinds.ExtractMemoriesKey(nytkaEvent.SubjectId), now, ct);
    }
}
