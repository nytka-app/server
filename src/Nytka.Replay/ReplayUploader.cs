using System.Net.Http.Headers;
using Nytka.Audio.Frames;

namespace Nytka.Replay;

public static class ReplayUploader
{
    /// <summary>Posts the chunks in order; stops at the first one the server refuses.</summary>
    public static async Task UploadAsync(HttpClient client, IReadOnlyList<Chunk> chunks, TextWriter log, CancellationToken ct)
    {
        foreach (var chunk in chunks)
        {
            using var content = new ByteArrayContent(ChunkFormat.Write(chunk));
            content.Headers.ContentType = new MediaTypeHeaderValue(ChunkFormat.MediaType);

            using var response = await client.PostAsync("api/v1/chunks", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"The server refused frames {chunk.FirstSeq}-{chunk.LastSeq} with {(int)response.StatusCode}: "
                    + await response.Content.ReadAsStringAsync(ct));
            }

            await log.WriteLineAsync($"Frames {chunk.FirstSeq}-{chunk.LastSeq}: {(int)response.StatusCode}");
        }
    }
}
