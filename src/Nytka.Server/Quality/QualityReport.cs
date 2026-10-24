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
    QualityItems Items, int UnconfirmedMemories, int? VoiceGroups, double? SingletonShare);

public sealed record QualityResponse(string TimeZone, string From, string To, IReadOnlyList<QualityDay> Days);

/// <summary>Turns stored counts into the rates of <c>GET /api/v1/quality</c> and <c>get_quality</c>.</summary>
public static class QualityReport
{
    public static QualityDay Day(QualityRow row)
    {
        var c = row.Counts;
        return new QualityDay(
            row.LocalDate, row.MeasuredAt, c.Conversations, c.Lines,
            Rate(c.Fragments, c.Conversations), Rate(c.Untitled, c.Conversations), Rate(c.Conversations - c.NonEmpty, c.Conversations),
            Rate(c.WearerLines, c.Lines), Rate(c.WearerConversations, c.NonEmpty), Rate(c.VoiceScoredLines, c.Lines),
            Rate(c.UnknownLines, c.Lines), Rate(c.RawLabelLines, c.Lines), Rate(c.StoredRawLabelLines, c.Lines),
            Rate(c.MediaConversations, c.NonEmpty), Rate(c.MediaLineConversations, c.NonEmpty), Rate(c.TaggedConversations, c.Conversations),
            new QualityItems(c.ItemsVital, c.ItemsOrdinary, c.ItemsNoise, c.ItemsUnrated), c.UnconfirmedMemories,
            c.VoiceGroups, c.VoiceGroups is { } groups && c.SingletonGroups is { } singletons ? Rate(singletons, groups) : null);
    }

    private static double? Rate(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 4);
}
