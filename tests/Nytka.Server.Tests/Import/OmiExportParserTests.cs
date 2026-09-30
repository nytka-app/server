using System.Text.Json;
using Nytka.Server.Import;

namespace Nytka.Server.Tests.Import;

public class OmiExportParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private static Nytka.Storage.ImportBatch Parse(string json) => OmiExportParser.Parse(JsonDocument.Parse(json).RootElement, Now);

    [Fact]
    public void A_negative_offset_is_taken_as_zero()
    {
        var batch = Parse(
            """
            { "conversations": [ { "id": "a", "started_at": "2026-03-01T09:00:00+00:00",
              "transcript_segments": [ { "text": "hi there", "start": -0.4, "end": -0.1 } ] } ] }
            """);

        var segment = Assert.Single(Assert.Single(batch.Conversations).Segments);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero), segment.StartedAt);
        Assert.Equal(segment.StartedAt, segment.EndedAt);
    }

    [Fact]
    public void A_time_with_a_Z_or_a_compact_offset_is_read()
    {
        var batch = Parse(
            """
            { "conversations": [
              { "id": "a", "started_at": "2026-03-01T09:00:00Z", "transcript_segments": [ { "text": "one" } ] },
              { "id": "b", "started_at": "2026-03-01T09:00:00-0500", "transcript_segments": [ { "text": "two" } ] } ] }
            """);

        Assert.Equal([9, 14], batch.Conversations.Select(c => c.StartedAt.Hour));
    }

    [Theory]
    [InlineData("short text", 300, "short text")]
    [InlineData("alpha beta gamma", 12, "alpha beta")]
    [InlineData("alpha beta gamma", 10, "alpha beta")]
    [InlineData("alphabetagamma", 5, "alpha")]
    public void CutAtWord_cuts_at_the_last_whole_word(string text, int max, string expected) =>
        Assert.Equal(expected, OmiExportParser.CutAtWord(text, max));
}
