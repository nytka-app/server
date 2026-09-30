using System.Globalization;
using System.Text.Json;

namespace Nytka.Audio.Vad;

/// <summary>
/// Weekly mute windows (the setting <c>mute.windows</c>): audio captured inside one never leaves the
/// server's pipeline. A window names its start weekdays (ISO, 1 = Monday to 7 = Sunday) and a local
/// start and end time; it crosses midnight when the end is not after the start. Local means the
/// user's time zone. Every conversion to UTC errs wide, so a DST change never leaves a stretch
/// unmuted: an ambiguous local time takes the earliest start and the latest end, a missing one
/// becomes the instant the clock jumps.
/// </summary>
public sealed class MuteWindows
{
    public const int MaxWindows = 50;

    public static MuteWindows None { get; } = new([]);

    private readonly IReadOnlyList<Window> _windows;

    private MuteWindows(IReadOnlyList<Window> windows) => _windows = windows;

    public bool IsEmpty => _windows.Count == 0;

    /// <summary>Null when the JSON is a valid list of windows, else what is wrong (never the value itself).</summary>
    public static string? Validate(string json) => TryParse(json, out _);

    /// <summary>Parses the setting; empty text means no windows. Returns null on success, else the reason.</summary>
    public static string? TryParse(string json, out MuteWindows windows)
    {
        windows = None;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return "Must be a JSON list such as [{\"days\":[1,2,3,4,5],\"start\":\"09:30\",\"end\":\"10:00\"}].";
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return "Must be a JSON list of windows.";
            }

            if (root.GetArrayLength() > MaxWindows)
            {
                return $"At most {MaxWindows} windows.";
            }

            var parsed = new List<Window>();
            foreach (var item in root.EnumerateArray())
            {
                if (ParseWindow(item, out var window) is { } error)
                {
                    return $"Window {parsed.Count + 1}: {error}";
                }

                parsed.Add(window);
            }

            windows = new MuteWindows(parsed);
            return null;
        }
    }

    /// <summary>
    /// The UTC stretches (ms since the Unix epoch) that touch [<paramref name="fromMs"/>, <paramref name="toMs"/>),
    /// merged and each widened by <paramref name="expandMs"/> on both sides.
    /// </summary>
    public IReadOnlyList<SpeechRegion> Intervals(TimeZoneInfo zone, long fromMs, long toMs, long expandMs = 0)
    {
        if (_windows.Count == 0 || toMs <= fromMs)
        {
            return [];
        }

        // A window starting a day early can still reach into the range, and so can one starting a day late once widened.
        var first = ToLocal(zone, fromMs - expandMs).Date.AddDays(-1);
        var last = ToLocal(zone, toMs + expandMs).Date.AddDays(1);
        var found = new List<SpeechRegion>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var isoDay = day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
            foreach (var window in _windows.Where(w => w.Days.Contains(isoDay)))
            {
                var startLocal = day + window.Start;
                var endLocal = day + window.End + (window.End <= window.Start ? TimeSpan.FromDays(1) : TimeSpan.Zero);
                var start = ToUtcMs(zone, startLocal, isStart: true) - expandMs;
                var end = ToUtcMs(zone, endLocal, isStart: false) + expandMs;
                if (end > start && end > fromMs && start < toMs)
                {
                    found.Add(new SpeechRegion(start, end));
                }
            }
        }

        return Merge(found);
    }

    private static List<SpeechRegion> Merge(List<SpeechRegion> intervals)
    {
        var merged = new List<SpeechRegion>();
        foreach (var interval in intervals.OrderBy(i => i.StartMs))
        {
            if (merged.Count > 0 && interval.StartMs <= merged[^1].EndMs)
            {
                merged[^1] = merged[^1] with { EndMs = Math.Max(merged[^1].EndMs, interval.EndMs) };
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }

    private static DateTime ToLocal(TimeZoneInfo zone, long utcMs) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime, zone);

    private static long ToUtcMs(TimeZoneInfo zone, DateTime local, bool isStart)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            offset = isStart ? offsets.Max() : offsets.Min();
        }
        else if (zone.IsInvalidTime(local))
        {
            return TransitionMs(zone, local);
        }
        else
        {
            offset = zone.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset).ToUnixTimeMilliseconds();
    }

    /// <summary>The first instant whose local time is at or after a local time the clock skipped.</summary>
    private static long TransitionMs(TimeZoneInfo zone, DateTime skipped)
    {
        var asUtc = new DateTimeOffset(DateTime.SpecifyKind(skipped, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var low = asUtc - TimeSpan.FromHours(30).TotalMilliseconds; // local < skipped here
        var high = asUtc + TimeSpan.FromHours(30).TotalMilliseconds; // local >= skipped here
        var lo = (long)low;
        var hi = (long)high;
        while (hi - lo > 1)
        {
            var mid = lo + (hi - lo) / 2;
            if (ToLocal(zone, mid) >= skipped)
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return hi;
    }

    private static string? ParseWindow(JsonElement item, out Window window)
    {
        window = default!;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return "must be an object with days, start and end.";
        }

        if (!item.TryGetProperty("days", out var daysElement) || daysElement.ValueKind != JsonValueKind.Array)
        {
            return "days must be a list of weekdays from 1 (Monday) to 7 (Sunday).";
        }

        var days = new HashSet<int>();
        foreach (var day in daysElement.EnumerateArray())
        {
            if (day.ValueKind != JsonValueKind.Number || !day.TryGetInt32(out var number) || number is < 1 or > 7)
            {
                return "days must be a list of weekdays from 1 (Monday) to 7 (Sunday).";
            }

            days.Add(number);
        }

        if (days.Count == 0)
        {
            return "days must not be empty.";
        }

        if (!TryTime(item, "start", out var start) || !TryTime(item, "end", out var end))
        {
            return "start and end must be times as HH:mm, 00:00 to 23:59.";
        }

        if (start == end)
        {
            return "start and end must differ.";
        }

        window = new Window(days, start, end);
        return null;
    }

    private static bool TryTime(JsonElement item, string name, out TimeSpan time)
    {
        time = default;
        return item.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && element.GetString() is { Length: 5 } text
            && TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out time);
    }

    private sealed record Window(HashSet<int> Days, TimeSpan Start, TimeSpan End);
}
