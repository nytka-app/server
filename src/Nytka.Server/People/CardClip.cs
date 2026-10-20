using Nytka.Audio.Frames;
using Nytka.Audio.Playback;
using Nytka.Server.Api;

namespace Nytka.Server.People;

/// <summary>Cuts a card's clip out of stored speech audio (docs/specs/people.md, Cards).</summary>
public static class CardClip
{
    /// <summary>
    /// The frames captured from <paramref name="from"/> up to <paramref name="until"/>, as one Ogg Opus stream, or null when none is
    /// stored. Frames are chosen by their capture times, never by counting samples (invariant 1), and at most
    /// <see cref="CardPicker.MaxClipMs"/> of them.
    /// </summary>
    public static byte[]? Cut(IEnumerable<byte[]> bodies, DateTime from, DateTime until, Guid cardId) =>
        Cut(bodies, from, until, BitConverter.ToUInt32(cardId.ToByteArray(), 12), CardPicker.MaxClipMs);

    /// <summary>As above, at most <paramref name="maxMs"/>, as the Ogg stream <paramref name="serial"/>.</summary>
    public static byte[]? Cut(IEnumerable<byte[]> bodies, DateTime from, DateTime until, uint serial, int maxMs)
    {
        long fromMs = new DateTimeOffset(from, TimeSpan.Zero).ToUnixTimeMilliseconds(), untilMs = new DateTimeOffset(until, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var frames = AudioEndpoints.ReadFrames(bodies)
            .Where(f => f.CapturedAtMs >= fromMs && f.CapturedAtMs < untilMs)
            .Take(maxMs / Frame.DurationMs)
            .ToList();
        return frames.Count == 0 ? null : OggOpusWriter.Write([.. frames.Select(f => f.Payload)], serial);
    }
}
