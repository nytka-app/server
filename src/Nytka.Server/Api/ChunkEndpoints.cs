using Nytka.Audio.Frames;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;

namespace Nytka.Server.Api;

public static class ChunkEndpoints
{
    public static RouteGroupBuilder MapChunks(this RouteGroupBuilder api)
    {
        api.MapPost("/chunks", UploadAsync);
        return api;
    }

    public sealed record UploadResponse(long AcceptedThroughSeq);

    private static async Task<IResult> UploadAsync(
        HttpRequest request, ChunkStore chunks, JobQueue jobs, TimeProvider time, CancellationToken ct)
    {
        if (request.ContentLength > ChunkFormat.MaxBytes)
        {
            return TooLarge();
        }

        var body = await ReadLimitedAsync(request.Body, ChunkFormat.MaxBytes, ct);
        if (body is null)
        {
            return TooLarge();
        }

        Chunk chunk;
        try
        {
            chunk = ChunkFormat.Read(body);
        }
        catch (ChunkFormatException error)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed chunk.", detail: error.Message);
        }

        var now = time.GetUtcNow();
        var response = new UploadResponse(chunk.LastSeq);
        switch (await chunks.StoreAsync(chunk, body, now, ct))
        {
            case StoreOutcome.Stored:
                await jobs.EnqueueAsync(
                    JobKinds.ProcessSession, new SessionPayload(chunk.Session), JobKinds.ProcessSessionKey(chunk.Session), now, ct);
                return Results.Accepted(value: response);
            case StoreOutcome.Duplicate:
                return Results.Ok(response);
            default:
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict, title: "The chunk overlaps frames the server already holds.");
        }
    }

    private static IResult TooLarge() => Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge, title: $"A chunk may not exceed {ChunkFormat.MaxBytes} bytes.");

    /// <summary>Reads the body, or returns null once it grows past <paramref name="limit"/>.</summary>
    internal static async Task<byte[]?> ReadLimitedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var block = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(block, 0, read);
        }

        return buffer.ToArray();
    }
}
