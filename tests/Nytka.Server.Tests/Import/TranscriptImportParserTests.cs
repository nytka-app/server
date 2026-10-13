using Nytka.Server.Import;

namespace Nytka.Server.Tests.Import;

public sealed class TranscriptImportParserTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Txt_becomes_one_segment_at_the_given_start_time()
    {
        var parsed = TranscriptImportParser.ParseTxt("  hello there  \n\nit's me  ", StartedAt);

        Assert.Equal(StartedAt, parsed.StartedAt);
        Assert.Equal(StartedAt, parsed.EndedAt);
        var segment = Assert.Single(parsed.Segments);
        Assert.Equal(StartedAt, segment.StartedAt);
        Assert.Equal(StartedAt, segment.EndedAt);
        Assert.Equal("hello there  \n\nit's me", segment.Text);
    }

    [Fact]
    public void Txt_strips_a_leading_byte_order_mark()
    {
        var parsed = TranscriptImportParser.ParseTxt("﻿hello", StartedAt);

        Assert.Equal("hello", Assert.Single(parsed.Segments).Text);
    }

    [Fact]
    public void Txt_rejects_an_empty_file()
    {
        Assert.Throws<TranscriptImportException>(() => TranscriptImportParser.ParseTxt("   \n  ", StartedAt));
    }

    [Fact]
    public void Srt_parses_cues_as_offsets_from_the_start_time()
    {
        const string srt = """
            1
            00:00:01,000 --> 00:00:02,500
            Hello there.

            2
            00:00:03,000 --> 00:00:04,000
            How are you?
            Doing fine.

            """;

        var parsed = TranscriptImportParser.ParseSrt(srt, StartedAt);

        Assert.Equal(2, parsed.Segments.Count);
        Assert.Equal(StartedAt.AddSeconds(1), parsed.Segments[0].StartedAt);
        Assert.Equal(StartedAt.AddSeconds(2.5), parsed.Segments[0].EndedAt);
        Assert.Equal("Hello there.", parsed.Segments[0].Text);
        Assert.Equal(StartedAt.AddSeconds(3), parsed.Segments[1].StartedAt);
        Assert.Equal("How are you?\nDoing fine.", parsed.Segments[1].Text);
        Assert.Equal(StartedAt, parsed.StartedAt);
        Assert.Equal(StartedAt.AddSeconds(4), parsed.EndedAt);
    }

    [Fact]
    public void Srt_rejects_a_malformed_timing_line()
    {
        const string srt = """
            1
            not a timing line
            Hello.

            """;

        Assert.Throws<TranscriptImportException>(() => TranscriptImportParser.ParseSrt(srt, StartedAt));
    }

    [Fact]
    public void Srt_rejects_a_file_with_no_cues()
    {
        Assert.Throws<TranscriptImportException>(() => TranscriptImportParser.ParseSrt("\n\n", StartedAt));
    }

    [Fact]
    public void Vtt_requires_the_WEBVTT_header()
    {
        const string vtt = """
            00:00:01.000 --> 00:00:02.000
            Hello.

            """;

        Assert.Throws<TranscriptImportException>(() => TranscriptImportParser.ParseVtt(vtt, StartedAt));
    }

    [Fact]
    public void Vtt_parses_cues_with_an_optional_hour_and_dot_separated_fraction()
    {
        const string vtt = """
            WEBVTT

            00:01.000 --> 00:02.500
            Hello there.

            NOTE this is skipped
            with more text

            00:03.000 --> 00:04.000
            Second cue.

            """;

        var parsed = TranscriptImportParser.ParseVtt(vtt, StartedAt);

        Assert.Equal(2, parsed.Segments.Count);
        Assert.Equal(StartedAt.AddSeconds(1), parsed.Segments[0].StartedAt);
        Assert.Equal("Hello there.", parsed.Segments[0].Text);
        Assert.Equal(StartedAt.AddSeconds(3), parsed.Segments[1].StartedAt);
        Assert.Equal("Second cue.", parsed.Segments[1].Text);
    }

    [Fact]
    public void An_end_time_before_its_start_is_treated_as_equal_to_it()
    {
        const string srt = """
            1
            00:00:05,000 --> 00:00:01,000
            Odd timing.

            """;

        var parsed = TranscriptImportParser.ParseSrt(srt, StartedAt);

        Assert.Equal(parsed.Segments[0].StartedAt, parsed.Segments[0].EndedAt);
    }
}
