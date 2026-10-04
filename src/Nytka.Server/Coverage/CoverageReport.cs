using System.Globalization;
using System.Text.Json;
using Nytka.Audio.Frames;
using Nytka.Server.Digests;
using Nytka.Storage;

namespace Nytka.Server.Coverage;

/// <summary>One diagnostics sample, reduced to what the coverage report reads. Counters are cumulative per capture session.</summary>
public sealed record CoverageSample(
    DateTimeOffset At,
    Guid? Session,
    string Connection,
    long LostNotifications,
    long DroppedFrames,
    long FramesQueued,
    long LostPackets,
    long MutedFrames,
    long? RingReadSeq,
    long? RingWriteSeq,
    long QueueChunks)
{
    /// <summary>The sample a stored row holds; a missing or non-numeric field reads as 0 (null for the ring positions).</summary>
    public static CoverageSample? Parse(DiagnosticRow row)
    {
        using var document = JsonDocument.Parse(row.Payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("connection", out var connection) || connection.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        Guid? session = root.TryGetProperty("session", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed)
            ? parsed
            : null;
        return new CoverageSample(
            new DateTimeOffset(DateTime.SpecifyKind(row.At, DateTimeKind.Utc)),
            session,
            connection.GetString()!,
            Count(root, "lostNotifications") ?? 0,
            Count(root, "droppedFrames") ?? 0,
            Count(root, "framesQueued") ?? 0,
            Count(root, "lostPackets") ?? 0,
            Count(root, "mutedFrames") ?? 0,
            Count(root, "ringReadSeq"),
            Count(root, "ringWriteSeq"),
            Count(root, "queueChunks") ?? 0);
    }

    private static long? Count(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number >= 0
            ? number
            : null;
}

public enum CoverageBucket
{
    Day,
    Hour,
}

/// <summary>Seconds of one bucket. Time states add up to the bucket's length; audio figures are estimates from frame and packet counts.</summary>
public sealed record CoverageSlice(
    string Start,
    double ConnectedS,
    double MutedS,
    double AwayS,
    double UnobservedS,
    double ReceivedS,
    double LostS,
    double DroppedByMuteS,
    double? Coverage);

/// <summary>
/// A stretch worth looking at. <see cref="Loss"/> says whether it counts against coverage; <see cref="AudioS"/> is the lost
/// audio for a loss, the audio still on its way for <c>pending</c>, the audio that arrived anyway for <c>away</c> and
/// <c>unobserved</c>, and null for <c>muted</c>.
/// </summary>
public sealed record CoverageGap(DateTimeOffset From, DateTimeOffset To, string Reason, bool Loss, double? AudioS);

public sealed record CoverageNow(DateTimeOffset? LastSampleAt, DateTimeOffset? LastAudioAt, double? PendingOnPendantS, long? QueuedChunksOnPhone);

public sealed record CoverageResponse(
    DateTimeOffset From,
    DateTimeOffset To,
    string TimeZone,
    string Bucket,
    CoverageSlice Totals,
    IReadOnlyList<CoverageSlice> Buckets,
    IReadOnlyList<CoverageGap> Gaps,
    int GapsTotal,
    CoverageNow Now,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Joins the diagnostics samples (what the phone saw: link state, lost notifications, queue) with the chunk rows (what
/// arrived) into the "Nothing is lost" figures of docs/specs/coverage.md. Silence never reaches the server, so a stretch
/// without audio is no loss by itself: only counters and holes in a session's sequence numbers are.
/// </summary>
public sealed class CoverageReport(TimeZoneInfo zone, CoverageBucket bucket, DateTimeOffset from, DateTimeOffset to)
{
    /// <summary>Samples come every 10 s; a longer silence means the app was not capturing, or its samples have not arrived.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    /// <summary>Loss windows closer than this are one gap in the list.</summary>
    public static readonly TimeSpan MergeWithin = TimeSpan.FromMinutes(1);

    /// <summary>The pendant's ring stores about 80 ms of audio in a packet (the app's own estimate).</summary>
    private const double PacketS = 0.08;

    private const double FrameS = Frame.DurationMs / 1000.0;

    private const string Muted = "muted";
    private const string Away = "away";
    private const string Unobserved = "unobserved";
    private const string LinkLoss = "link-loss";
    private const string RingLost = "ring-lost";
    private const string MissingChunk = "missing-chunk";
    private const string NotArrived = "not-arrived";
    private const string Pending = "pending";

    private readonly Dictionary<string, Accumulator> _buckets = [];
    private readonly List<Gap> _gaps = [];

    public static CoverageResponse Build(
        IReadOnlyList<CoverageSample> samples, IReadOnlyList<ChunkSpan> chunks, DateTimeOffset from, DateTimeOffset to,
        TimeZoneInfo zone, CoverageBucket bucket, DateTimeOffset now, int limit) =>
        new CoverageReport(zone, bucket, from, to).Run(samples.OrderBy(s => s.At).ToList(), chunks, now, limit);

    private CoverageResponse Run(List<CoverageSample> samples, IReadOnlyList<ChunkSpan> chunks, DateTimeOffset now, int limit)
    {
        foreach (var slice in Slices(from, to))
        {
            Bucket(slice.Label);
        }

        AddTimeStates(samples);
        AddCounterLosses(samples);
        AddReceived(chunks);
        AddSequenceLosses(samples, chunks);
        AddAudioToTimeGaps(chunks);

        var gaps = MergeLosses().OrderBy(g => g.From).ThenBy(g => g.Reason, StringComparer.Ordinal).ToList();
        var totals = new Accumulator();
        foreach (var accumulator in _buckets.Values)
        {
            totals.Add(accumulator);
        }

        var last = samples.Count > 0 ? samples[^1] : null;
        var ring = last is { RingReadSeq: { } read, RingWriteSeq: { } write } && write >= read ? (write - read) * PacketS : (double?)null;
        var newest = chunks.Count > 0 ? chunks.Max(c => c.LastTime) : (DateTime?)null;
        return new CoverageResponse(
            from, to, zone.Id, bucket.ToString().ToLowerInvariant(),
            totals.ToSlice("total"),
            _buckets.Select(b => b.Value.ToSlice(b.Key)).ToList(),
            gaps.Take(limit).Select(g => g.ToGap()).ToList(),
            gaps.Count,
            new CoverageNow(
                last?.At,
                newest is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null,
                ring is { } seconds ? Round(seconds) : null,
                last?.QueueChunks),
            Warnings(samples, now));
    }

    private List<string> Warnings(List<CoverageSample> samples, DateTimeOffset now)
    {
        var warnings = new List<string>();
        if (!samples.Any(s => s.At >= from && s.At < to))
        {
            warnings.Add("No diagnostics samples in this range: turn on \"Send diagnostics to my server\" in the app's developer settings.");
        }
        else if (now - samples[^1].At > StaleAfter && to >= now - StaleAfter)
        {
            warnings.Add("The newest sample is older than 30 seconds: the app is not capturing, or the phone holds samples it has not uploaded yet.");
        }

        if (from < now - Pipeline.RetentionHandler.ChunkRowsKept)
        {
            warnings.Add("The range starts before chunk rows are kept (7 days): received audio before then is undercounted.");
        }

        return warnings;
    }

    /// <summary>Splits the time between samples into connected, muted, away and unobserved stretches.</summary>
    private void AddTimeStates(List<CoverageSample> samples)
    {
        if (samples.Count == 0 || samples[0].At - from > StaleAfter)
        {
            AddState(Unobserved, from, samples.Count == 0 ? to : samples[0].At);
        }

        for (var i = 0; i < samples.Count; i++)
        {
            var end = i + 1 < samples.Count ? samples[i + 1].At : to;
            if (end - samples[i].At > StaleAfter)
            {
                AddState(Unobserved, samples[i].At, end);
            }
            else
            {
                AddState(samples[i].Connection, samples[i].At, end);
            }
        }
    }

    private void AddState(string state, DateTimeOffset start, DateTimeOffset end)
    {
        start = Max(start, from);
        end = Min(end, to);
        if (end <= start)
        {
            return;
        }

        var reason = state switch
        {
            "connected" => null,
            Muted => Muted,
            "connecting" or "disconnected" or "refused" or Away => Away,
            _ => Unobserved,
        };
        foreach (var slice in Slices(start, end))
        {
            var seconds = (slice.End - slice.Start).TotalSeconds;
            var accumulator = Bucket(slice.Label);
            switch (reason)
            {
                case null:
                    accumulator.ConnectedS += seconds;
                    break;
                case Muted:
                    accumulator.MutedS += seconds;
                    break;
                case Away:
                    accumulator.AwayS += seconds;
                    break;
                default:
                    accumulator.UnobservedS += seconds;
                    break;
            }
        }

        if (reason is null)
        {
            return;
        }

        // Neighbouring intervals of one state are one gap.
        if (_gaps.LastOrDefault(g => g.Reason == reason) is { } open && start - open.To <= TimeSpan.FromSeconds(1))
        {
            open.To = end;
            return;
        }

        _gaps.Add(new Gap(start, end, reason, loss: false, audioS: reason == Muted ? null : 0));
    }

    /// <summary>Lost notifications and dropped frames on the live link, and ring packets the pendant freed before the phone read them.</summary>
    private void AddCounterLosses(List<CoverageSample> samples)
    {
        var previous = new Dictionary<Guid, CoverageSample>();
        foreach (var sample in samples)
        {
            if (sample.Session is not { } session)
            {
                continue;
            }

            previous.TryGetValue(session, out var before);
            previous[session] = sample;
            if (sample.At < from || sample.At > to)
            {
                continue;
            }

            var start = before?.At ?? sample.At;
            var frames = Math.Max(Step(sample.LostNotifications, before?.LostNotifications), Step(sample.DroppedFrames, before?.DroppedFrames));
            if (frames > 0)
            {
                AddLoss(LinkLoss, start, sample.At, frames * FrameS);
            }

            if (Step(sample.LostPackets, before?.LostPackets) is > 0 and var packets)
            {
                AddLoss(RingLost, start, sample.At, packets * PacketS);
            }

            if (Step(sample.MutedFrames, before?.MutedFrames) is > 0 and var muted)
            {
                Spread(sample.At, sample.At, muted * FrameS, (a, seconds) => a.DroppedByMuteS += seconds);
            }
        }
    }

    /// <summary>A counter that falls started again at 0 (a new run of the capture service).</summary>
    private static long Step(long now, long? before) => before is not { } b ? now : now >= b ? now - b : now;

    private void AddReceived(IReadOnlyList<ChunkSpan> chunks)
    {
        foreach (var chunk in chunks)
        {
            Spread(Start(chunk), End(chunk), chunk.FrameCount * FrameS, (a, seconds) => a.ReceivedS += seconds);
        }
    }

    /// <summary>
    /// Frames a session numbered but the server does not hold: a hole between two chunks, or frames after the last one that
    /// the app's counter says it queued. The newest session's tail may still be on its way.
    /// </summary>
    private void AddSequenceLosses(List<CoverageSample> samples, IReadOnlyList<ChunkSpan> chunks)
    {
        var bySession = samples.Where(s => s.Session is not null).GroupBy(s => s.Session!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var latest = samples.LastOrDefault(s => s.Session is not null)?.Session;
        var chunksBySession = chunks.GroupBy(c => c.Session).ToDictionary(g => g.Key, g => g.OrderBy(c => c.FirstSeq).ToList());
        foreach (var session in bySession.Keys.Union(chunksBySession.Keys))
        {
            bySession.TryGetValue(session, out var seen);
            chunksBySession.TryGetValue(session, out var held);
            var expected = 0L;
            DateTimeOffset? lastAudio = null;
            foreach (var chunk in held ?? [])
            {
                if (chunk.FirstSeq > expected)
                {
                    var start = lastAudio ?? seen?[0].At ?? Start(chunk);
                    AddLoss(MissingChunk, start, Max(start, Start(chunk)), (chunk.FirstSeq - expected) * FrameS);
                }

                expected = Math.Max(expected, chunk.FirstSeq + chunk.FrameCount);
                lastAudio = End(chunk);
            }

            var queued = seen?.Max(s => s.FramesQueued) ?? 0;
            if (queued > expected)
            {
                var start = lastAudio ?? seen![0].At;
                var tail = (queued - expected) * FrameS;
                if (session == latest)
                {
                    AddGap(Pending, start, Max(start, seen![^1].At), loss: false, tail);
                }
                else
                {
                    AddLoss(NotArrived, start, Max(start, seen![^1].At), tail);
                }
            }
        }
    }

    /// <summary>Tells whether audio arrived during a stretch the app was away or unobserved: stored audio is uploaded afterwards.</summary>
    private void AddAudioToTimeGaps(IReadOnlyList<ChunkSpan> chunks)
    {
        foreach (var gap in _gaps.Where(g => g.Reason is Away or Unobserved))
        {
            var seconds = 0.0;
            foreach (var chunk in chunks)
            {
                var start = Max(Start(chunk), gap.From);
                var end = Min(End(chunk), gap.To);
                if (end > start)
                {
                    seconds += chunk.FrameCount * FrameS * (end - start) / (End(chunk) - Start(chunk));
                }
            }

            gap.AudioS = seconds;
        }
    }

    private void AddLoss(string reason, DateTimeOffset start, DateTimeOffset end, double seconds)
    {
        AddGap(reason, start, end, loss: true, seconds);
        Spread(start, end, seconds, (a, part) => a.LostS += part);
    }

    private void AddGap(string reason, DateTimeOffset start, DateTimeOffset end, bool loss, double? seconds)
    {
        if (end >= from && start <= to)
        {
            _gaps.Add(new Gap(start, end, reason, loss, seconds));
        }
    }

    private IEnumerable<Gap> MergeLosses()
    {
        foreach (var group in _gaps.GroupBy(g => g.Loss || g.Reason == Pending ? g.Reason : null))
        {
            if (group.Key is null)
            {
                foreach (var gap in group)
                {
                    yield return gap;
                }

                continue;
            }

            Gap? open = null;
            foreach (var gap in group.OrderBy(g => g.From))
            {
                if (open is not null && gap.From - open.To <= MergeWithin)
                {
                    open.To = Max(open.To, gap.To);
                    open.AudioS += gap.AudioS;
                    continue;
                }

                if (open is not null)
                {
                    yield return open;
                }

                open = gap;
            }

            if (open is not null)
            {
                yield return open;
            }
        }
    }

    /// <summary>Adds <paramref name="value"/> to the buckets the span touches, in proportion to the time in each; the part outside the range is dropped.</summary>
    private void Spread(DateTimeOffset start, DateTimeOffset end, double value, Action<Accumulator, double> add)
    {
        if (end <= start)
        {
            if (start >= from && start <= to)
            {
                // At the very end of the range the point belongs to the last bucket, not the next one.
                var point = start == to ? start.AddTicks(-1) : start;
                add(Bucket(Slices(point, point.AddTicks(1)).First().Label), value);
            }

            return;
        }

        var total = (end - start).TotalSeconds;
        foreach (var slice in Slices(Max(start, from), Min(end, to)))
        {
            add(Bucket(slice.Label), value * (slice.End - slice.Start).TotalSeconds / total);
        }
    }

    private Accumulator Bucket(string label)
    {
        if (!_buckets.TryGetValue(label, out var accumulator))
        {
            _buckets[label] = accumulator = new Accumulator();
        }

        return accumulator;
    }

    /// <summary>The span cut at local day (or hour) boundaries.</summary>
    private IEnumerable<(string Label, DateTimeOffset Start, DateTimeOffset End)> Slices(DateTimeOffset start, DateTimeOffset end)
    {
        var cursor = start;
        while (cursor < end)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, zone);
            string label;
            DateTimeOffset next;
            if (bucket == CoverageBucket.Day)
            {
                var day = DateOnly.FromDateTime(local.DateTime);
                label = DigestDay.Text(day);
                next = DigestDay.Bounds(day, zone).To;
            }
            else
            {
                var hour = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
                label = hour.ToString("yyyy-MM-dd'T'HH':00'", CultureInfo.InvariantCulture);
                next = EndOfHour(hour);
            }

            if (next <= cursor)
            {
                next = cursor.AddHours(1);
            }

            var sliceEnd = Min(next, end);
            yield return (label, cursor, sliceEnd);
            cursor = sliceEnd;
        }
    }

    private DateTimeOffset EndOfHour(DateTime hour)
    {
        var local = hour.AddHours(1);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static DateTimeOffset Start(ChunkSpan chunk) => new(DateTime.SpecifyKind(chunk.BaseTime, DateTimeKind.Utc));

    /// <summary>A chunk row's <c>last_time</c> is the end of its last frame.</summary>
    private static DateTimeOffset End(ChunkSpan chunk) => new(DateTime.SpecifyKind(chunk.LastTime, DateTimeKind.Utc));

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static double Round(double seconds) => Math.Round(seconds, 2);

    private sealed class Gap(DateTimeOffset start, DateTimeOffset end, string reason, bool loss, double? audioS)
    {
        public DateTimeOffset From { get; } = start;

        public DateTimeOffset To { get; set; } = end;

        public string Reason { get; } = reason;

        public bool Loss { get; } = loss;

        public double? AudioS { get; set; } = audioS;

        public CoverageGap ToGap() => new(From, To, Reason, Loss, AudioS is { } seconds ? Round(seconds) : null);
    }

    private sealed class Accumulator
    {
        public double ConnectedS { get; set; }

        public double MutedS { get; set; }

        public double AwayS { get; set; }

        public double UnobservedS { get; set; }

        public double ReceivedS { get; set; }

        public double LostS { get; set; }

        public double DroppedByMuteS { get; set; }

        public void Add(Accumulator other)
        {
            ConnectedS += other.ConnectedS;
            MutedS += other.MutedS;
            AwayS += other.AwayS;
            UnobservedS += other.UnobservedS;
            ReceivedS += other.ReceivedS;
            LostS += other.LostS;
            DroppedByMuteS += other.DroppedByMuteS;
        }

        public CoverageSlice ToSlice(string start) => new(
            start, Round(ConnectedS), Round(MutedS), Round(AwayS), Round(UnobservedS), Round(ReceivedS), Round(LostS), Round(DroppedByMuteS),
            ReceivedS + LostS > 0 ? Math.Round(ReceivedS / (ReceivedS + LostS), 5) : null);
    }
}
