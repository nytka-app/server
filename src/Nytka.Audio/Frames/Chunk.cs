namespace Nytka.Audio.Frames;

/// <summary>Frames the app uploads in one request. Sequence numbers run FirstSeq, FirstSeq + 1, ...</summary>
public sealed record Chunk(Guid Session, byte Codec, uint FirstSeq, long BaseTimeMs, IReadOnlyList<Frame> Frames)
{
    public uint LastSeq => FirstSeq + (uint)Frames.Count - 1;
}
