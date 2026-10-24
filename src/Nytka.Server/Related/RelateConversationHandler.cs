using System.Text.Json;
using Nytka.Server.Jobs;
using Nytka.Server.Search;
using Nytka.Storage;

namespace Nytka.Server.Related;

/// <summary>The payload of <c>relate-conversation</c>.</summary>
public sealed record RelatePayload(Guid ConversationId);

/// <summary>
/// <c>relate-conversation</c> (Ai lane): works out a conversation's "Earlier" links again (<see cref="RelatedRule"/>) and clears
/// its stale mark. No model call, only the stored vectors; without them the job waits for the scheduler to queue it again.
/// </summary>
public sealed class RelateConversationHandler(RelatedStore related, EmbeddingState embeddings, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.RelateConversation;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (!embeddings.IsReady)
        {
            return JobOutcome.Done;
        }

        var payload = JsonSerializer.Deserialize<RelatePayload>(job.Payload)!;
        var candidates = await related.CandidatesAsync(payload.ConversationId, RelatedRule.Candidates, ct);
        await related.ReplaceAsync(payload.ConversationId, RelatedRule.Links(candidates), time.GetUtcNow(), ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
}
