using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;
using Nytka.Audio.Frames;
using Nytka.Audio.Playback;
using Nytka.Server.Auth;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// Playback (docs/specs/v0.8.md): a conversation's speech audio as one Ogg Opus stream, and the map from stream
/// positions to capture times. Both need <c>read</c>. The stream is rebuilt from the stored Opus packets on each
/// request and is identical every time, so range requests and <c>If-Range</c> work without keeping a copy.
/// </summary>
public static class AudioEndpoints
{
    public const string OggMediaType = "audio/ogg";

    /// <summary>The largest Opus packet; the chunk format allows more, and a longer frame is not audio.</summary>
    public const int MaxOpusPacketBytes = 1275;

    public static RouteGroupBuilder MapAudio(this RouteGroupBuilder api)
    {
        var audio = api.MapGroup("/conversations/{id:guid}/audio");
        audio.MapGet("", StreamAsync).AllowRead();
        audio.MapGet("/index", IndexAsync).AllowRead();
        return api;
    }

    public sealed record IndexRun(int OffsetMs, DateTime StartedAt, DateTime EndedAt);

    public sealed record AudioIndex(int DurationMs, IReadOnlyList<IndexRun> Runs);

    private static async Task<IResult> StreamAsync(Guid id, BatchStore batches, CancellationToken ct)
    {
        var frames = await FramesAsync(id, batches, ct);
        if (frames.Count == 0)
        {
            return NotFound();
        }

        var body = OggOpusWriter.Write([.. frames.Select(f => f.Payload)], Serial(id));
        var tag = Convert.ToHexString(SHA256.HashData(body).AsSpan(0, 16));
        return Results.File(body, OggMediaType, entityTag: new EntityTagHeaderValue($"\"{tag}\""), enableRangeProcessing: true);
    }

    private static async Task<IResult> IndexAsync(Guid id, BatchStore batches, CancellationToken ct)
    {
        var frames = await FramesAsync(id, batches, ct);
        if (frames.Count == 0)
        {
            return NotFound();
        }

        var map = PlaybackMap.Build(frames);
        return Results.Ok(new AudioIndex(
            map.DurationMs,
            [.. map.Runs.Select(r => new IndexRun(
                r.OffsetMs, DateTimeOffset.FromUnixTimeMilliseconds(r.StartMs).UtcDateTime, DateTimeOffset.FromUnixTimeMilliseconds(r.EndMs).UtcDateTime))]));
    }

    /// <summary>The frames of every stored piece, in order. A piece that does not parse and a frame over the Opus maximum are skipped, never reported.</summary>
    private static async Task<List<Frame>> FramesAsync(Guid id, BatchStore batches, CancellationToken ct)
    {
        var frames = new List<Frame>();
        foreach (var body in await batches.SpeechAudioBodiesAsync(id, ct))
        {
            try
            {
                frames.AddRange(ChunkFormat.Read(body).Frames.Where(f => f.Payload.Length <= MaxOpusPacketBytes));
            }
            catch (ChunkFormatException)
            {
            }
        }

        return frames;
    }

    /// <summary>A stable stream serial from the conversation id.</summary>
    private static uint Serial(Guid id) => BitConverter.ToUInt32(id.ToByteArray(), 12);

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No audio for this conversation.");
}
