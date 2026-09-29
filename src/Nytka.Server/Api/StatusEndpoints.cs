using Nytka.Storage;

namespace Nytka.Server.Api;

public static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatus(this RouteGroupBuilder api)
    {
        api.MapGet("/status", async (ChunkStore chunks, BatchStore batches, CancellationToken ct) =>
        {
            var pending = await chunks.PendingAsync(ct);
            return Results.Ok(new StatusResponse(pending.PendingChunks, pending.OldestPendingAt, await batches.LastErrorAsync(ct)));
        });
        return api;
    }

    public sealed record StatusResponse(long PendingChunks, DateTime? OldestPendingAt, string? LastError);
}
