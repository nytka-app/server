using Nytka.Audio.Batching;

namespace Nytka.Audio.Tests.Batching;

public class OffsetMapTests
{
    private static readonly OffsetMap Map = new(
        [new OffsetMap.Entry(0, 1_000), new OffsetMap.Entry(1_000, 5_000)],
        totalMs: 1_500);

    [Theory]
    [InlineData(0.0, 1_000)]
    [InlineData(0.5, 1_500)]
    [InlineData(1.2, 5_200)]
    [InlineData(9.0, 5_500)] // past the end: clamps to the end of the audio
    [InlineData(-1.0, 1_000)]
    public void Maps_batch_offsets_to_capture_times(double offsetSeconds, long captureMs) =>
        Assert.Equal(captureMs, Map.ToCaptureMs(offsetSeconds));

    [Fact]
    public void Round_trips_through_json()
    {
        var copy = OffsetMap.FromJson(Map.ToJson());

        Assert.Equal(Map.Entries, copy.Entries);
        Assert.Equal(Map.TotalMs, copy.TotalMs);
    }
}
