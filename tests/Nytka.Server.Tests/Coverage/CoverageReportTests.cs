using Nytka.Server.Coverage;
using Nytka.Storage;

namespace Nytka.Server.Tests.Coverage;

public sealed class CoverageReportTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Day.AddDays(3);
    private static readonly Guid Run = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Next = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static CoverageSample Sample(
        DateTimeOffset at, string connection = "connected", Guid? session = null, long lost = 0, long dropped = 0, long queued = 0,
        long lostPackets = 0, long muted = 0) =>
        new(at, session ?? Run, connection, lost, dropped, queued, lostPackets, muted, null, null, 0);

    /// <summary>One sample every 10 s from <paramref name="start"/> for <paramref name="seconds"/>.</summary>
    private static IEnumerable<CoverageSample> Every10s(DateTimeOffset start, int seconds, string connection = "connected", Guid? session = null) =>
        Enumerable.Range(0, seconds / 10 + 1).Select(i => Sample(start.AddSeconds(i * 10), connection, session));

    private static ChunkSpan Chunk(Guid session, long firstSeq, int frames, DateTimeOffset start) =>
        new(session, firstSeq, frames, start.UtcDateTime, start.AddMilliseconds(frames * 20).UtcDateTime);

    /// <summary>The range runs from the first sample to the last unless a test says otherwise.</summary>
    private static CoverageResponse Build(
        IEnumerable<CoverageSample> samples, IEnumerable<ChunkSpan> chunks, DateTimeOffset? to = null, string zone = "UTC",
        CoverageBucket bucket = CoverageBucket.Day, DateTimeOffset? from = null)
    {
        var list = samples.ToList();
        return CoverageReport.Build(
            list, chunks.ToList(), from ?? (list.Count > 0 ? list.Min(s => s.At) : Day), to ?? (list.Count > 0 ? list.Max(s => s.At) : Day.AddHours(1)),
            TimeZoneInfo.FindSystemTimeZoneById(zone), bucket, Now, 500);
    }

    [Fact]
    public void A_clean_hour_is_fully_covered()
    {
        var samples = Every10s(Day, 3600).Select((s, i) => s with { FramesQueued = i * 100 }).ToList();
        var chunks = Enumerable.Range(0, 360).Select(i => Chunk(Run, i * 100, 100, Day.AddSeconds(i * 10))).ToList();

        var report = Build(samples, chunks);

        Assert.Equal(3600, report.Totals.ConnectedS);
        Assert.Equal(720, report.Totals.ReceivedS, 1);
        Assert.Equal(0, report.Totals.LostS);
        Assert.Equal(1.0, report.Totals.Coverage);
        Assert.Empty(report.Gaps);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Lost_notifications_are_a_link_loss_of_20_ms_each()
    {
        var at = Day.AddMinutes(1);
        var samples = new[] { Sample(at), Sample(at.AddSeconds(10), lost: 50), Sample(at.AddSeconds(20), lost: 50) };
        var chunks = new[] { Chunk(Run, 0, 500, at) };

        var report = Build(samples, chunks);

        var gap = Assert.Single(report.Gaps, g => g.Loss);
        Assert.Equal(("link-loss", 1.0), (gap.Reason, gap.AudioS));
        Assert.Equal(1.0, report.Totals.LostS);
        Assert.Equal(10.0 / 11.0, report.Totals.Coverage!.Value, 3);
    }

    [Fact]
    public void Dropped_frames_and_lost_notifications_of_one_loss_count_once()
    {
        var at = Day.AddMinutes(1);

        var report = Build([Sample(at), Sample(at.AddSeconds(10), lost: 3, dropped: 3)], []);

        Assert.Equal(0.06, report.Totals.LostS, 2);
    }

    [Fact]
    public void A_counter_that_falls_or_a_new_session_starts_again_at_zero()
    {
        var at = Day.AddMinutes(1);
        var samples = new[]
        {
            Sample(at, lost: 10),
            Sample(at.AddSeconds(10), lost: 10),
            Sample(at.AddSeconds(20), lost: 5),
            Sample(at.AddSeconds(30), session: Next, lost: 2),
        };

        var report = Build(samples, []);

        // 10 at the first sample, 0, 5 after the reset, 2 in the new session.
        Assert.Equal(17 * 0.02, report.Totals.LostS, 3);
    }

    [Fact]
    public void Ring_packets_the_pendant_freed_are_a_loss_of_80_ms_each()
    {
        var at = Day.AddMinutes(1);

        var report = Build([Sample(at), Sample(at.AddSeconds(10), lostPackets: 25)], []);

        var gap = Assert.Single(report.Gaps);
        Assert.Equal(("ring-lost", true, 2.0), (gap.Reason, gap.Loss, gap.AudioS));
    }

    [Fact]
    public void Mute_is_not_a_loss_and_dropped_stored_frames_stay_out_of_coverage()
    {
        var at = Day.AddMinutes(1);
        var samples = Every10s(at, 60, "muted").Concat([Sample(at.AddSeconds(70), muted: 500)]).ToList();

        var report = Build(samples, []);

        Assert.Equal(70, report.Totals.MutedS);
        Assert.Equal(10, report.Totals.DroppedByMuteS);
        Assert.Equal(0, report.Totals.LostS);
        Assert.Null(report.Totals.Coverage);
        var gap = Assert.Single(report.Gaps);
        Assert.Equal(("muted", false, (double?)null), (gap.Reason, gap.Loss, gap.AudioS));
    }

    [Fact]
    public void Away_time_reports_the_audio_that_synced_for_it_and_is_no_loss()
    {
        var away = Day.AddMinutes(1);
        var samples = Every10s(away, 120, "disconnected").Concat([Sample(away.AddSeconds(130))]).ToList();
        var stored = Chunk(Next, 0, 1500, away.AddSeconds(20));

        var report = Build(samples, [stored]);

        Assert.Equal(130, report.Totals.AwayS);
        Assert.Equal(30, report.Totals.ReceivedS);
        var gap = Assert.Single(report.Gaps);
        Assert.Equal(("away", false, 30.0), (gap.Reason, gap.Loss, gap.AudioS));
        Assert.Equal(1.0, report.Totals.Coverage);
    }

    [Fact]
    public void A_stretch_without_samples_is_unobserved_and_no_samples_at_all_warns()
    {
        var start = Day;
        var samples = Every10s(start, 60).Concat(Every10s(start.AddMinutes(10), 60)).ToList();

        var report = Build(samples, [], Day.AddMinutes(11), from: Day);

        Assert.Equal(120, report.Totals.ConnectedS);
        Assert.Equal(540, report.Totals.UnobservedS);
        var gap = Assert.Single(report.Gaps);
        Assert.Equal(("unobserved", false), (gap.Reason, gap.Loss));
        Assert.Equal((Day.AddSeconds(60), Day.AddMinutes(10)), (gap.From, gap.To));

        var none = Build([], [], Day.AddHours(1), from: Day);

        Assert.Equal(3600, none.Totals.UnobservedS);
        Assert.Contains("No diagnostics samples", Assert.Single(none.Warnings));
    }

    [Fact]
    public void A_hole_in_the_sequence_numbers_is_a_missing_chunk()
    {
        var at = Day.AddMinutes(1);
        var chunks = new[] { Chunk(Run, 0, 100, at), Chunk(Run, 200, 100, at.AddSeconds(8)) };

        var report = Build([Sample(at, queued: 300), Sample(at.AddSeconds(20), queued: 300)], chunks);

        var gap = Assert.Single(report.Gaps);
        Assert.Equal(("missing-chunk", true, 2.0), (gap.Reason, gap.Loss, gap.AudioS));
        Assert.Equal(4.0, report.Totals.ReceivedS, 2);
        Assert.Equal(2.0, report.Totals.LostS);
    }

    [Fact]
    public void Frames_queued_beyond_the_last_chunk_are_lost_in_an_older_session_and_pending_in_the_newest()
    {
        var at = Day.AddMinutes(1);
        var samples = new[]
        {
            Sample(at, queued: 150), Sample(at.AddSeconds(10), queued: 150),
            Sample(at.AddMinutes(5), session: Next, queued: 40), Sample(at.AddMinutes(5).AddSeconds(10), session: Next, queued: 40),
        };
        var chunks = new[] { Chunk(Run, 0, 100, at), Chunk(Next, 0, 30, at.AddMinutes(5)) };

        var report = Build(samples, chunks);

        var lost = Assert.Single(report.Gaps, g => g.Reason == "not-arrived");
        Assert.Equal((true, 1.0), (lost.Loss, lost.AudioS));
        var pending = Assert.Single(report.Gaps, g => g.Reason == "pending");
        Assert.Equal((false, 0.2), (pending.Loss, pending.AudioS));
        Assert.Equal(1.0, report.Totals.LostS);
    }

    [Fact]
    public void Losses_a_minute_apart_are_one_gap()
    {
        var at = Day.AddMinutes(1);
        var samples = Enumerable.Range(0, 6).Select(i => Sample(at.AddSeconds(i * 10), lost: i * 5)).ToList();

        var report = Build(samples, []);

        var gap = Assert.Single(report.Gaps);
        Assert.Equal((at, at.AddSeconds(50)), (gap.From, gap.To));
        Assert.Equal(0.5, gap.AudioS!.Value, 2);
    }

    [Fact]
    public void Time_is_split_at_local_midnight()
    {
        var start = new DateTimeOffset(2026, 10, 1, 20, 0, 0, TimeSpan.Zero); // 23:00 in Kyiv (UTC+3)
        var samples = Every10s(start, 7200);

        var report = Build(samples, [], start.AddHours(2), "Europe/Kyiv", from: start);

        Assert.Equal(["2026-10-01", "2026-10-02"], report.Buckets.Select(b => b.Start));
        Assert.Equal([3600, 3600], report.Buckets.Select(b => b.ConnectedS));
    }

    [Fact]
    public void Hour_buckets_cover_the_range()
    {
        var report = Build(Every10s(Day, 7200), [], Day.AddHours(2), bucket: CoverageBucket.Hour);

        Assert.Equal(["2026-10-01T00:00", "2026-10-01T01:00"], report.Buckets.Select(b => b.Start));
        Assert.Equal("hour", report.Bucket);
    }

    [Fact]
    public void Audio_before_the_range_is_not_counted()
    {
        var chunk = Chunk(Run, 0, 1500, Day.AddSeconds(-15)); // 30 s, half of it before

        var report = Build(Every10s(Day, 60), [chunk], Day.AddMinutes(1));

        Assert.Equal(15, report.Totals.ReceivedS, 1);
    }

    [Fact]
    public void Parse_reads_a_sample_and_skips_log_lines()
    {
        var row = new DiagnosticRow(
            new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
            """{"id":"x","at":"2026-10-01T08:00:00Z","session":"00000000-0000-0000-0000-000000000001","connection":"connected","lostNotifications":7,"droppedFrames":"bad","ringWriteSeq":90,"ringReadSeq":10,"queueChunks":2}""");

        var sample = CoverageSample.Parse(row)!;

        Assert.Equal((Run, "connected", 7L, 0L), (sample.Session, sample.Connection, sample.LostNotifications, sample.DroppedFrames));
        Assert.Equal((90L, 10L, 2L), (sample.RingWriteSeq, sample.RingReadSeq, sample.QueueChunks));
        Assert.Null(CoverageSample.Parse(row with { Payload = """{"kind":"log","message":"x"}""" }));
    }
}
