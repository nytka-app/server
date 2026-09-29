using System.Globalization;
using System.Net.Http.Headers;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Wav;

namespace Nytka.Replay;

/// <summary>
/// Sends a WAV file (or a file of chunks, such as the app's developer-mode fixtures) to a Nytka
/// server, for testing without a pendant.
/// </summary>
public static class ReplayCommand
{
    private const string Usage =
        "Usage: Nytka.Replay --server <url> (--wav <file> | --chunks <file>) [--token <token>] [--start <ISO 8601 time>]\n"
        + "       Nytka.Replay --out <file> --wav <file> [--start <ISO 8601 time>]\n"
        + "The token falls back to the NYTKA_TOKEN environment variable. --out writes the chunks to a file instead;\n"
        + "--chunks sends chunks written back to back, keeping their session, sequence numbers and times.";

    private const string Convert = "Convert it first: ffmpeg -i in -ar 16000 -ac 1 -sample_fmt s16 out.wav";

    public static async Task<int> Main(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            options[args[i]] = args[i + 1];
        }

        var token = options.GetValueOrDefault("--token") ?? Environment.GetEnvironmentVariable("NYTKA_TOKEN");
        var server = options.GetValueOrDefault("--server");
        var output = options.GetValueOrDefault("--out");
        var wav = options.GetValueOrDefault("--wav");
        var chunkFile = options.GetValueOrDefault("--chunks");
        var valid = args.Length % 2 == 0
            && (wav is null) != (chunkFile is null)
            && (output is null ? server is not null && !string.IsNullOrEmpty(token) : wav is not null);
        if (!valid)
        {
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }

        IReadOnlyList<Chunk> chunks;
        if (chunkFile is not null)
        {
            chunks = ReplayChunker.ReadAll(await File.ReadAllBytesAsync(chunkFile));
        }
        else
        {
            WavAudio audio;
            try
            {
                audio = WavReader.Read(await File.ReadAllBytesAsync(wav!));
            }
            catch (InvalidDataException error)
            {
                await Console.Error.WriteLineAsync($"{error.Message} {Convert}");
                return 2;
            }

            if (audio.SampleRate != Timeline.SampleRate || audio.Channels != 1)
            {
                await Console.Error.WriteLineAsync(
                    $"The file is {audio.SampleRate} Hz with {audio.Channels} channel(s); Nytka needs 16 kHz mono. {Convert}");
                return 2;
            }

            var length = TimeSpan.FromSeconds((double)audio.Samples.Length / Timeline.SampleRate);
            var start = options.TryGetValue("--start", out var at)
                ? DateTimeOffset.Parse(at, CultureInfo.InvariantCulture)
                : DateTimeOffset.UtcNow - length;
            chunks = ReplayChunker.Build(audio.Samples, Guid.CreateVersion7(), start.ToUnixTimeMilliseconds());
        }

        var duration = TimeSpan.FromMilliseconds(chunks.Sum(c => c.Frames.Count) * (double)Frame.DurationMs);

        if (output is not null)
        {
            await File.WriteAllBytesAsync(output, chunks.SelectMany(ChunkFormat.Write).ToArray());
            Console.WriteLine($"Wrote {chunks.Count} chunk(s), {duration:hh\\:mm\\:ss} of audio, to {output}.");
            return 0;
        }

        using var client = new HttpClient { BaseAddress = new Uri(server!.TrimEnd('/') + "/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            await ReplayUploader.UploadAsync(client, chunks, Console.Out, CancellationToken.None);
        }
        catch (HttpRequestException error)
        {
            await Console.Error.WriteLineAsync(error.Message);
            return 1;
        }

        Console.WriteLine(
            $"Sent {chunks.Count} chunk(s), {duration:hh\\:mm\\:ss} of audio. "
            + "Transcripts appear once the session has been idle for a minute.");
        return 0;
    }
}
