using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Api;

public static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatus(this RouteGroupBuilder api)
    {
        api.MapGet("/status", async (
            ChunkStore chunks, BatchStore batches, ConversationStore conversations, ILlmClient llm, CancellationToken ct) =>
        {
            var pending = await chunks.PendingAsync(ct);
            var outcomes = await batches.OutcomesAsync(ct);
            var runs = await conversations.AiRunStatusAsync(EnrichConversationHandler.TooShort, ct);

            // A failed run is trouble only until a later run finishes; the time stays as history.
            var aiFailureIsCurrent = runs.FailedAt is { } failedAt && (runs.FinishedAt is not { } finishedAt || failedAt > finishedAt);
            return Results.Ok(new StatusResponse(
                pending.PendingChunks, pending.OldestPendingAt, outcomes.LastError, outcomes.LastErrorAt, outcomes.LastSuccessAt,
                new AiStatus(llm.IsConfigured, runs.Pending, aiFailureIsCurrent ? runs.FailedMessage : null, runs.FailedAt)));
        });
        return api;
    }

    public sealed record AiStatus(bool Configured, long Pending, string? LastError, DateTime? LastErrorAt);

    public sealed record StatusResponse(
        long PendingChunks, DateTime? OldestPendingAt, string? LastError, DateTime? LastErrorAt, DateTime? LastSuccessAt, AiStatus Ai);
}
