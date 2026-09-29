using Nytka.Audio.Frames;

namespace Nytka.Audio.Tests.Frames;

public class ChunkFormatTests
{
    // The Android plan's ChunkWriterTest uses the same bytes. Change both or neither.
    private const string GoldenHex =
        "4e59544b011500112233445566778899aabbccddeeff0700000000d78792990100000200000000000000030001020314000000"
        + "02000405";

    private static readonly Chunk Golden = new(
        Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        ChunkFormat.OpusFs320,
        FirstSeq: 7,
        BaseTimeMs: 1_759_100_000_000,
        Frames:
        [
            new Frame(7, 1_759_100_000_000, new byte[] { 1, 2, 3 }),
            new Frame(8, 1_759_100_000_020, new byte[] { 4, 5 }),
        ]);

    private static byte[] GoldenBytes() => Convert.FromHexString(GoldenHex);

    [Fact]
    public void Reads_the_golden_chunk()
    {
        var chunk = ChunkFormat.Read(GoldenBytes());

        Assert.Equal(Golden.Session, chunk.Session);
        Assert.Equal(7u, chunk.FirstSeq);
        Assert.Equal(8u, chunk.LastSeq);
        Assert.Equal(1_759_100_000_000, chunk.BaseTimeMs);
        Assert.Equal(2, chunk.Frames.Count);
        Assert.Equal(8u, chunk.Frames[1].Seq);
        Assert.Equal(1_759_100_000_020, chunk.Frames[1].CapturedAtMs);
        Assert.Equal(new byte[] { 4, 5 }, chunk.Frames[1].Payload.ToArray());
    }

    [Fact]
    public void Writes_the_golden_chunk() =>
        Assert.Equal(GoldenHex, Convert.ToHexString(ChunkFormat.Write(Golden)).ToLowerInvariant());

    [Fact]
    public void Round_trips() =>
        Assert.Equal(GoldenBytes(), ChunkFormat.Write(ChunkFormat.Read(GoldenBytes())));

    [Theory]
    [InlineData(3, 0x4C, "NYTK")]      // magic
    [InlineData(4, 0x02, "version")]   // version
    [InlineData(5, 0x14, "Codec")]     // codec 20
    public void Rejects_a_bad_header(int index, byte value, string message)
    {
        var bytes = GoldenBytes();
        bytes[index] = value;

        var error = Assert.Throws<ChunkFormatException>(() => ChunkFormat.Read(bytes));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_truncated_chunk() =>
        Assert.Contains("truncated", Assert.Throws<ChunkFormatException>(
            () => ChunkFormat.Read(GoldenBytes()[..^1])).Message, StringComparison.Ordinal);

    [Fact]
    public void Rejects_trailing_bytes() =>
        Assert.Contains("trailing", Assert.Throws<ChunkFormatException>(
            () => ChunkFormat.Read([.. GoldenBytes(), 0])).Message, StringComparison.Ordinal);

    [Fact]
    public void Rejects_a_chunk_without_frames()
    {
        var bytes = GoldenBytes()[..ChunkFormat.HeaderSize];
        bytes[34] = 0;

        Assert.Contains("no frames", Assert.Throws<ChunkFormatException>(
            () => ChunkFormat.Read(bytes)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_more_than_1500_frames()
    {
        var bytes = GoldenBytes();
        BitConverter.TryWriteBytes(bytes.AsSpan(34, 4), 1501u);

        Assert.Contains("1500", Assert.Throws<ChunkFormatException>(
            () => ChunkFormat.Read(bytes)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_chunk_above_512_KiB()
    {
        var bytes = new byte[ChunkFormat.MaxBytes + 1];
        GoldenBytes().CopyTo(bytes, 0);

        Assert.Contains("limit", Assert.Throws<ChunkFormatException>(
            () => ChunkFormat.Read(bytes)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_to_write_frames_out_of_sequence()
    {
        var broken = Golden with { Frames = [Golden.Frames[0], Golden.Frames[1] with { Seq = 9 }] };

        Assert.Throws<ArgumentException>(() => ChunkFormat.Write(broken));
    }

    [Fact]
    public void Refuses_to_write_a_frame_captured_before_the_base_time()
    {
        var broken = Golden with { BaseTimeMs = Golden.BaseTimeMs + 1 };

        Assert.Throws<ArgumentException>(() => ChunkFormat.Write(broken));
    }
}
