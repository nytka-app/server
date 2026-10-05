using Nytka.Storage;

namespace Nytka.Server.Speech;

/// <summary>What the phone's context ranges say about a stretch (docs/specs/speech-kind.md, Phone prior and Calls).</summary>
public static class PhoneContext
{
    /// <summary>The share of a stretch a media range must cover to add the prior.</summary>
    public const double MediaCover = 0.5;

    /// <summary>A call is far-side speech only with a wearer line this close to the stretch.</summary>
    public const double CallWearerSeconds = 3;

    private const string Speaker = "speaker";
    private const string MediaKind = "media";
    private const string CallKind = "call";

    /// <summary>Whether phone media ranges on the loudspeaker together cover at least half of the stretch.</summary>
    public static bool MediaCovers(Stretch stretch, IReadOnlyList<ContextRangeRow> ranges) =>
        Covered(stretch, ranges, MediaKind) >= MediaCover;

    /// <summary>
    /// Whether the stretch is the far side of a call: speaker-route call ranges cover all of it and a wearer line lies within
    /// 3 s of it (<paramref name="wearerSeconds"/>, from <see cref="Stretches.DistanceSeconds"/>). Without that line the score decides.
    /// </summary>
    public static bool IsCall(Stretch stretch, IReadOnlyList<ContextRangeRow> ranges, double wearerSeconds) =>
        wearerSeconds <= CallWearerSeconds && Covered(stretch, ranges, CallKind) >= 1;

    /// <summary>The share of the stretch the loudspeaker ranges of <paramref name="kind"/> cover, their overlaps counted once.</summary>
    private static double Covered(Stretch stretch, IReadOnlyList<ContextRangeRow> ranges, string kind)
    {
        var mine = ranges.Where(r => r.Kind == kind && r.Route == Speaker).OrderBy(r => r.StartedAt).ToList();
        var length = (stretch.End - stretch.Start).TotalSeconds;
        if (length <= 0)
        {
            return mine.Any(r => r.StartedAt <= stretch.Start && r.EndedAt >= stretch.Start) ? 1 : 0;
        }

        var covered = 0.0;
        var from = stretch.Start;
        foreach (var range in mine)
        {
            var start = range.StartedAt > from ? range.StartedAt : from;
            var end = range.EndedAt < stretch.End ? range.EndedAt : stretch.End;
            if (end > start)
            {
                covered += (end - start).TotalSeconds;
                from = end;
            }
        }

        return covered / length;
    }
}
