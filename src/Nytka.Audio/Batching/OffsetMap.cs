using System.Text.Json;

namespace Nytka.Audio.Batching;

/// <summary>
/// Maps a time inside a batch's audio back to a capture time. Silence was cut out of the batch,
/// so the map holds one entry per stretch of continuous audio.
/// </summary>
public sealed class OffsetMap
{
    public OffsetMap(IReadOnlyList<Entry> entries, long totalMs)
    {
        if (entries.Count == 0)
        {
            throw new ArgumentException("An offset map needs at least one entry.", nameof(entries));
        }

        Entries = entries;
        TotalMs = totalMs;
    }

    public IReadOnlyList<Entry> Entries { get; }

    public long TotalMs { get; }

    public long ToCaptureMs(double offsetSeconds)
    {
        var offset = Math.Clamp((long)Math.Round(offsetSeconds * 1000), 0, TotalMs);
        var entry = Entries[0];
        foreach (var candidate in Entries)
        {
            if (candidate.OffsetMs > offset)
            {
                break;
            }

            entry = candidate;
        }

        return entry.CaptureMs + (offset - entry.OffsetMs);
    }

    public string ToJson() => JsonSerializer.Serialize(new Stored(Entries.ToArray(), TotalMs));

    public static OffsetMap FromJson(string json)
    {
        var stored = JsonSerializer.Deserialize<Stored>(json)
            ?? throw new InvalidDataException("The offset map is empty.");
        return new OffsetMap(stored.Entries, stored.TotalMs);
    }

    public readonly record struct Entry(long OffsetMs, long CaptureMs);

    private sealed record Stored(Entry[] Entries, long TotalMs);
}
