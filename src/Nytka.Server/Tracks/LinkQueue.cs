using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;

namespace Nytka.Server.Tracks;

/// <summary>
/// What a build of a day does for links by meaning: it queues <c>link-speakers</c> for every scene (<see cref="SceneSplit"/>) of the
/// conversations that started on the day and have no run yet. A scene already asked about, skipped or given up on has a run, so a
/// repeated build queues nothing; the dedupe key makes a repeated call safe while a job waits.
/// </summary>
public sealed class LinkQueue(LinkStore links, TodayStore today, JobQueue queue, TimeProvider time)
{
    /// <summary>The number of scenes queued.</summary>
    public async Task<int> QueueAsync(DateOnly day, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var conversations = await links.ConversationsAsync(from.UtcDateTime, to.UtcDateTime, ct);
        if (conversations.Count == 0)
        {
            return 0;
        }

        var ids = conversations.Select(c => c.Id).ToArray();
        var spans = await today.SpansAsync(ids, ct);
        var done = await links.RunsAsync(ids, ct);
        var text = DigestDay.Text(day);
        var queued = 0;
        foreach (var conversation in conversations)
        {
            var span = spans[conversation.Id];
            var scenes = SceneSplit.Split(conversation.StartedAt, conversation.EndedAt ?? conversation.StartedAt, span.Events, span.Boundaries);
            for (var i = 0; i < scenes.Count; i++)
            {
                if (done.Contains((conversation.Id, scenes[i].StartedAt)))
                {
                    continue;
                }

                // The last scene has no end: lines may follow the conversation's recorded end.
                var payload = new LinkSpeakersPayload(conversation.Id, scenes[i].StartedAt, i == scenes.Count - 1 ? null : scenes[i].EndedAt, text);
                await queue.EnqueueAsync(
                    JobKinds.LinkSpeakers, payload, JobKinds.LinkSpeakersKey(conversation.Id, scenes[i].StartedAt), time.GetUtcNow(), ct);
                queued++;
            }
        }

        return queued;
    }
}
