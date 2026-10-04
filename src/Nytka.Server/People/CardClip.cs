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
    public static byte[]? Cut(IEnumerable<byte[]> bodies, DateTime from, DateTime until, Guid cardId)
    {
        long fromMs = new DateTimeOffset(from, TimeSpan.Zero).ToUnixTimeMilliseconds(), untilMs = new DateTimeOffset(until, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var frames = AudioEndpoints.ReadFrames(bodies)
            .Where(f => f.CapturedAtMs >= fromMs && f.CapturedAtMs < untilMs)
            .Take(CardPicker.MaxClipMs / Frame.DurationMs)
            .ToList();
        return frames.Count == 0 ? null : OggOpusWriter.Write([.. frames.Select(f => f.Payload)], BitConverter.ToUInt32(cardId.ToByteArray(), 12));
    }
}
