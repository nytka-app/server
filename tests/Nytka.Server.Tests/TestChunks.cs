using System.Net.Http.Headers;
using Nytka.Audio.Frames;

namespace Nytka.Server.Tests;

/// <summary>Chunks with placeholder payloads, for tests that never decode the audio.</summary>
public static class TestChunks
{
    public static byte[] Build(Guid session, uint firstSeq, int frames, long baseMs = 1_759_100_000_000) =>
        ChunkFormat.Write(new Chunk(
            session,
            ChunkFormat.OpusFs320,
            firstSeq,
            baseMs,
            Enumerable.Range(0, frames)
                .Select(i => new Frame(firstSeq + (uint)i, baseMs + i * Frame.DurationMs, new byte[] { 1, 2, 3 }))
                .ToList()));

    public static HttpContent Content(byte[] chunk)
    {
        var content = new ByteArrayContent(chunk);
        content.Headers.ContentType = new MediaTypeHeaderValue(ChunkFormat.MediaType);
        return content;
    }
}
