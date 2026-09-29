using Microsoft.Extensions.Options;
using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Pipeline;

/// <summary>Ends a conversation once its last speech is older than the gap, even when no new audio arrives.</summary>
public sealed class CloseConversationsHandler(ConversationStore conversations, IOptions<NytkaOptions> options, TimeProvider time)
    : IJobHandler
{
    public string Kind => JobKinds.CloseConversations;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await conversations.CloseIdleAsync(now - options.Value.Conversations.Gap, now, ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
}
