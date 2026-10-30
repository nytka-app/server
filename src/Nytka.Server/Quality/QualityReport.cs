using Nytka.Storage;

namespace Nytka.Server.Quality;

/// <summary>Items extracted on the day by salience; <paramref name="Unrated"/> have none (made by hand or before salience existed).</summary>
public sealed record QualityItems(int Vital, int Ordinary, int Noise, int Unrated);

/// <summary>
/// One measured local day of <c>GET /api/v1/quality</c>. Every rate is from 0 to 1, null when its base is 0: conversations for
/// the fragment, untitled, empty and tagged rates; conversations with a line for the wearer-present and media rates; lines (segments
/// with text) for the line shares; voice groups for <paramref name="SingletonShare"/>.
/// </summary>
public sealed record QualityDay(
    string Date, DateTime MeasuredAt, int Conversations, int Lines, double? FragmentRate, double? UntitledRate, double? EmptyRate,
    double? WearerLineShare, double? WearerConversationShare, double? VoiceScoredShare, double? UnknownSpeakerShare,
    double? RawLabelShare, double? StoredRawLabelShare, double? MediaShare, double? MediaLineShare, double? TaggedShare,
    QualityItems Items, int UnconfirmedMemories, int? VoiceGroups, double? SingletonShare,
    QualityAttribution? Attribution, QualityConversations ConversationStats, QualityWindow Last7Days);

/// <summary>Per class, the wearer's, a media line's, a person's, a raw provider label's and no label's, as <see cref="QualityCounts"/> defines them.</summary>
public sealed record QualityClasses<T>(T Wearer, T Person, T Label, T Media, T None);

/// <summary>Distinct raw provider labels per conversation with a line; null with no such conversation.</summary>
public sealed record QualityLabels(double? Mean, int? P50, int? P90);

/// <summary>
/// How a day's lines were attributed. <paramref name="ClassShare"/> is the share of lines (0 to 1, null with no line),
/// <paramref name="SpeechSeconds"/> their speech time, <paramref name="UnmappedMinutes"/> the speech minutes on raw labels
/// that no person owns (the <c>label</c> class). Null on a day measured before these existed.
/// </summary>
public sealed record QualityAttribution(
    QualityClasses<double?> ClassShare, QualityClasses<double> SpeechSeconds, QualityLabels LabelsPerConversation, double UnmappedMinutes);

/// <summary><paramref name="ShortShare"/> is the share of conversations under <see cref="QualityStore.FragmentSeconds"/> s, null with none.</summary>
public sealed record QualityConversations(double? ShortShare, double PerDay);

/// <summary>
/// The value of the day and the six before it, pooled over the measured days among them: shares and percentiles from the sums
/// and the lists of all those days, <c>perDay</c> and <c>unmappedMinutes</c> as a mean per day. <paramref name="AttributionDays"/>
/// counts the days that have attribution metrics, <paramref name="Days"/> every measured day.
/// </summary>
public sealed record QualityWindow(int Days, int AttributionDays, QualityAttribution? Attribution, QualityConversations Conversations);

public sealed record QualityResponse(string TimeZone, string From, string To, IReadOnlyList<QualityDay> Days);

/// <summary>Turns stored counts into the rates of <c>GET /api/v1/quality</c> and <c>get_quality</c>.</summary>
public static class QualityReport
{
    public const int WindowDays = 7;

    /// <summary><paramref name="row"/> with its window: the measured days among it and the six before it, in any order.</summary>
    public static QualityDay Day(QualityRow row, IReadOnlyList<QualityRow> measured)
    {
        var c = row.Counts;
        var end = DateOnly.ParseExact(row.LocalDate, "yyyy-MM-dd");
        var window = measured.Where(r => DateOnly.ParseExact(r.LocalDate, "yyyy-MM-dd") is var d && d <= end && d > end.AddDays(-WindowDays))
            .Select(r => r.Counts).ToList();
        var withAttribution = window.Where(w => w.LabelsPerConversation is not null).ToList();
        return new QualityDay(
            row.LocalDate, row.MeasuredAt, c.Conversations, c.Lines,
            Rate(c.Fragments, c.Conversations), Rate(c.Untitled, c.Conversations), Rate(c.Conversations - c.NonEmpty, c.Conversations),
            Rate(c.WearerLines, c.Lines), Rate(c.WearerConversations, c.NonEmpty), Rate(c.VoiceScoredLines, c.Lines),
            Rate(c.UnknownLines, c.Lines), Rate(c.RawLabelLines, c.Lines), Rate(c.StoredRawLabelLines, c.Lines),
            Rate(c.MediaConversations, c.NonEmpty), Rate(c.MediaLineConversations, c.NonEmpty), Rate(c.TaggedConversations, c.Conversations),
            new QualityItems(c.ItemsVital, c.ItemsOrdinary, c.ItemsNoise, c.ItemsUnrated), c.UnconfirmedMemories,
            c.VoiceGroups, c.VoiceGroups is { } groups && c.SingletonGroups is { } singletons ? Rate(singletons, groups) : null,
            Attribution([c]), Conversations([c], 1),
            new QualityWindow(window.Count, withAttribution.Count, Attribution(withAttribution), Conversations(window, window.Count)));
    }

    private static QualityAttribution? Attribution(IReadOnlyList<QualityCounts> days)
    {
        if (days.Count == 0 || days.Any(d => d.LabelsPerConversation is null))
        {
            return null;
        }

        int Lines(Func<QualityCounts, int?> pick) => days.Sum(d => pick(d) ?? 0);
        double Secs(Func<QualityCounts, double?> pick) => days.Sum(d => pick(d) ?? 0);

        var lines = days.Sum(d => d.Lines);
        var share = new QualityClasses<double?>(
            Rate(days.Sum(d => d.WearerLines), lines), Rate(Lines(d => d.PersonLines), lines), Rate(Lines(d => d.LabelLines), lines),
            Rate(Lines(d => d.MediaLines), lines), Rate(Lines(d => d.NoneLines), lines));
        var seconds = new QualityClasses<double>(
            Seconds(Secs(d => d.WearerSeconds)), Seconds(Secs(d => d.PersonSeconds)), Seconds(Secs(d => d.LabelSeconds)),
            Seconds(Secs(d => d.MediaSeconds)), Seconds(Secs(d => d.NoneSeconds)));

        var labels = days.SelectMany(d => d.LabelsPerConversation!).Order().ToList();
        var perConversation = labels.Count == 0
            ? new QualityLabels(null, null, null)
            : new QualityLabels(Math.Round(labels.Average(), 4), Percentile(labels, 0.5), Percentile(labels, 0.9));
        return new QualityAttribution(share, seconds, perConversation, Math.Round(Secs(d => d.LabelSeconds) / 60 / days.Count, 4));
    }

    private static QualityConversations Conversations(IReadOnlyList<QualityCounts> days, int count) =>
        new(Rate(days.Sum(d => d.Fragments), days.Sum(d => d.Conversations)),
            count == 0 ? 0 : Math.Round((double)days.Sum(d => d.Conversations) / count, 2));

    /// <summary>The nearest-rank percentile of <paramref name="sorted"/>, which is not empty.</summary>
    private static int Percentile(List<int> sorted, double p) => sorted[(int)Math.Ceiling(p * sorted.Count) - 1];

    private static double Seconds(double value) => Math.Round(value, 1);

    private static double? Rate(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 4);
}
