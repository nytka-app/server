using Nytka.Server.Speech;
using Nytka.Storage;

namespace Nytka.Server.Tests.Speech;

/// <summary>The phone prior and the call rule over context ranges (docs/specs/speech-kind.md, Phone prior and Calls).</summary>
public class PhoneContextTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    private static readonly Stretch Ten = new([new SpeechLine(1, T0, T0.AddSeconds(10), false)], 0);

    private static ContextRangeRow Range(string kind, string route, double start, double end) =>
        new(Guid.NewGuid(), kind, route, T0.AddSeconds(start), T0.AddSeconds(end));

    [Fact]
    public void A_media_range_on_the_speaker_covering_half_the_stretch_counts()
    {
        Assert.True(PhoneContext.MediaCovers(Ten, [Range("media", "speaker", 5, 30)]));
        Assert.False(PhoneContext.MediaCovers(Ten, [Range("media", "speaker", 5.1, 30)]));
    }

    [Fact]
    public void Ranges_that_overlap_are_counted_once()
    {
        Assert.False(PhoneContext.MediaCovers(Ten, [Range("media", "speaker", 0, 4), Range("media", "speaker", 1, 4)]));
        Assert.True(PhoneContext.MediaCovers(Ten, [Range("media", "speaker", 0, 3), Range("media", "speaker", 2, 6)]));
    }

    [Theory]
    [InlineData("media", "headset")]
    [InlineData("call", "speaker")]
    public void Only_a_media_range_on_the_speaker_counts_for_the_prior(string kind, string route)
    {
        Assert.False(PhoneContext.MediaCovers(Ten, [Range(kind, route, -5, 20)]));
    }

    [Fact]
    public void A_call_on_the_speaker_covering_the_stretch_with_a_wearer_line_within_three_seconds_is_a_call()
    {
        Assert.True(PhoneContext.IsCall(Ten, [Range("call", "speaker", -1, 11)], 3));
    }

    [Theory]
    [InlineData("call", "earpiece", 1)]
    [InlineData("call", "bluetooth", 1)]
    [InlineData("media", "speaker", 1)]
    [InlineData("call", "speaker", 3.1)]
    [InlineData("call", "speaker", 3600)]
    public void Another_route_or_kind_or_no_wearer_line_close_by_is_not_a_call(string kind, string route, double wearerSeconds)
    {
        Assert.False(PhoneContext.IsCall(Ten, [Range(kind, route, -1, 11)], wearerSeconds));
    }

    [Fact]
    public void A_call_that_covers_only_part_of_the_stretch_is_not_a_call()
    {
        Assert.False(PhoneContext.IsCall(Ten, [Range("call", "speaker", 0, 9)], 1));
    }
}
