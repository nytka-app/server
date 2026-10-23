using System.Text.Json;
using Nytka.Audio.Batching;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Wav;

namespace Nytka.Server.Pipeline;

/// <summary>A stretch of continuous captured audio, in capture milliseconds: <c>From</c> is its first sample's time, <c>To</c> the end of its last.</summary>
public sealed record PassStretch(long From, long To);

/// <summary>
/// The audio of one transcript pass: the stretches cut from the stored speech frames and joined with <see cref="GapMs"/> of
/// silence into one 16 kHz WAV, and the <see cref="OffsetMap"/> that turns a time in that WAV back into a capture time. Nothing here
/// divides a sample index by the sample rate to get a time: <see cref="Timeline"/> and the map do (docs/specs/v0.1.md, invariant 1).
/// </summary>
public sealed record PassAudio(byte[] Wav, OffsetMap Map, IReadOnlyList<PassStretch> Stretches)
{
    public const int GapMs = 300;

    private const int SamplesPerMs = Timeline.SampleRate / 1000;

    /// <summary>The runs of consecutive frames the timeline holds: a stretch ends where capture time jumps.</summary>
    public static IReadOnlyList<PassStretch> StretchesOf(Timeline timeline)
    {
        var stretches = new List<PassStretch>();
        var frames = timeline.Samples.Length / Timeline.SamplesPerFrame;
        var from = 0L;
        var previous = 0L;
        for (var i = 0; i < frames; i++)
        {
            var start = timeline.CaptureMsAt(i * Timeline.SamplesPerFrame);
            if (i == 0)
            {
                from = start;
            }
            else if (start != previous + Frame.DurationMs)
            {
                stretches.Add(new PassStretch(from, previous + Frame.DurationMs));
                from = start;
            }

            previous = start;
        }

        if (frames > 0)
        {
            stretches.Add(new PassStretch(from, previous + Frame.DurationMs));
        }

        return stretches;
    }

    /// <summary>Cuts the stretches out of the timeline. Null when none of them holds a sample.</summary>
    public static PassAudio? Cut(Timeline timeline, IReadOnlyList<PassStretch> stretches)
    {
        var samples = new List<short>();
        var entries = new List<OffsetMap.Entry>();
        var gap = new short[GapMs * SamplesPerMs];
        foreach (var stretch in stretches)
        {
            var start = timeline.SampleIndexAt(stretch.From);
            var end = timeline.SampleIndexAt(stretch.To);
            if (end <= start)
            {
                continue;
            }

            if (samples.Count > 0)
            {
                samples.AddRange(gap);
            }

            entries.Add(new OffsetMap.Entry(samples.Count / SamplesPerMs, timeline.CaptureMsAt(start)));
            samples.AddRange(timeline.Samples.AsSpan(start, end - start));
        }

        return entries.Count == 0
            ? null
            : new PassAudio(WavWriter.Write(samples.ToArray()), new OffsetMap(entries, samples.Count / SamplesPerMs), stretches);
    }

    public static string StretchesJson(IReadOnlyList<PassStretch> stretches) =>
        JsonSerializer.Serialize(stretches.Select(s => new { from = s.From, to = s.To }));

    public static IReadOnlyList<PassStretch> StretchesFromJson(string json) =>
        [.. JsonSerializer.Deserialize<List<StoredStretch>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Select(s => new PassStretch(s.From, s.To))];

    private sealed record StoredStretch(long From, long To);
}
