using Nytka.Server.Pipeline;
using Nytka.Storage;

namespace Nytka.Server.Jobs;

/// <summary>
/// Queues the jobs nothing else queues: closing idle conversations, retention, and another look at
/// every session that still holds chunk audio (speech waiting for more audio, or for the session
/// to go idle). Dedupe keys make every call safe to repeat.
/// </summary>
public sealed class Scheduler(JobQueue queue, ChunkStore chunks, TimeProvider time)
{
    public async Task TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await queue.EnqueueAsync(JobKinds.CloseConversations, new { }, JobKinds.CloseConversations, now, ct);
        await queue.EnqueueAsync(JobKinds.Retention, new { }, JobKinds.Retention, now, ct);

        foreach (var session in await chunks.PendingSessionsAsync(ct))
        {
            // A late session keeps its priority, or the re-queue would move it ahead of live speech.
            await queue.EnqueueAsync(
                JobKinds.ProcessSession, new SessionPayload(session.Id), JobKinds.ProcessSessionKey(session.Id), now, ct,
                session.Late ? JobPriority.Late : JobPriority.Live);
        }
    }
}
