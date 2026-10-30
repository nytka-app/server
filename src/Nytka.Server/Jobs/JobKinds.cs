using Nytka.Storage;

namespace Nytka.Server.Jobs;

public static class JobKinds
{
    public const string ProcessSession = "process-session";
    public const string Transcribe = "transcribe";
    public const string TranscribeConversation = "transcribe-conversation";
    public const string CloseConversations = "close-conversations";
    public const string Retention = "retention";
    public const string EnrichConversation = "enrich-conversation";
    public const string ExtractMemories = "extract-memories";
    public const string DeliverWebhook = "deliver-webhook";
    public const string MakeDigest = "make-digest";
    public const string RescoreVoice = "rescore-voice";
    public const string RematchVoice = "rematch-voice";
    public const string ApplySpeech = "apply-speech";
    public const string ClassifySpeech = "classify-speech";
    public const string SuggestNames = "suggest-names";
    public const string GroupVoices = "group-voices";
    public const string ExtractPersonFacts = "extract-person-facts";
    public const string SyncCalendar = "sync-calendar";
    public const string MakeBrief = "make-brief";
    public const string TidyTags = "tidy-tags";
    public const string MeasureQuality = "measure-quality";
    public const string MeasureInsights = "measure-insights";
    public const string PickLabeling = "pick-labeling";
    public const string FoldTopic = "fold-topic";
    public const string MakeRollup = "make-rollup";
    public const string RelateConversation = "relate-conversation";
    public const string ClassifyMemories = "classify-memories";
    public const string ClassifyConversations = "classify-conversations";
    public const string RecomputeEvents = "recompute-events";
    public const string BuildTracks = "build-tracks";
    public const string LinkSpeakers = "link-speakers";
    public const string NameTracks = "name-tracks";
    public const string BuildSituations = "build-situations";
    public const string EnrichSituation = "enrich-situation";
    public const string Rethink = "rethink";

    /// <summary>
    /// The kinds that run outside the Audio lane. Every other kind, v0.1's four, transcribe-conversation, rescore-voice, rematch-voice, apply-speech, classify-speech, pick-labeling,
    /// recompute-events, build-tracks, name-tracks, build-situations, rethink and one nobody handles alike, runs in Audio, so an unknown kind still fails and gives up like any job.
    /// </summary>
    private static readonly Dictionary<string, JobLane> Claimed = new()
    {
        [EnrichConversation] = JobLane.Ai,
        [ExtractMemories] = JobLane.Ai,
        [MakeDigest] = JobLane.Ai,
        [SuggestNames] = JobLane.Ai,
        [ExtractPersonFacts] = JobLane.Ai,
        [MakeBrief] = JobLane.Ai,
        [TidyTags] = JobLane.Ai,
        [MeasureQuality] = JobLane.Ai,
        [MeasureInsights] = JobLane.Ai,
        [FoldTopic] = JobLane.Ai,
        [MakeRollup] = JobLane.Ai,
        [RelateConversation] = JobLane.Ai,
        [ClassifyMemories] = JobLane.Ai,
        [ClassifyConversations] = JobLane.Ai,
        [LinkSpeakers] = JobLane.Ai,
        [EnrichSituation] = JobLane.Ai,
        [SyncCalendar] = JobLane.Hooks,
        [DeliverWebhook] = JobLane.Hooks,
    };

    public static string ProcessSessionKey(Guid session) => $"{ProcessSession}:{session}";

    public static string TranscribeKey(long batchId) => $"{Transcribe}:{batchId}";

    public static string TranscribeConversationKey(Guid conversation) => $"{TranscribeConversation}:{conversation}";

    public static string EnrichConversationKey(Guid conversation) => $"{EnrichConversation}:{conversation}";

    public static string ExtractMemoriesKey(Guid conversation) => $"{ExtractMemories}:{conversation}";

    public static string ClassifySpeechKey(Guid conversation) => $"{ClassifySpeech}:{conversation}";

    public static string SuggestNamesKey(Guid conversation) => $"{SuggestNames}:{conversation}";
    public static string ExtractPersonFactsKey(Guid conversation) => $"{ExtractPersonFacts}:{conversation}";

    public static string FoldTopicKey(Guid topic) => $"{FoldTopic}:{topic}";

    /// <summary>One rollup per period, start and topic at a time.</summary>
    public static string MakeRollupKey(string period, string? start, Guid? topic) => $"{MakeRollup}:{period}:{start}:{topic}";
    public static string RelateConversationKey(Guid conversation) => $"{RelateConversation}:{conversation}";

    public static string TidyTagsKey(string localDate) => $"{TidyTags}:{localDate}";

    public static string MakeBriefKey(string uid, DateTimeOffset startsAt) => $"{MakeBrief}:{uid}:{startsAt.ToUnixTimeSeconds()}";

    /// <summary>One measurement per local date at a time.</summary>
    public static string MeasureQualityKey(string localDate) => $"{MeasureQuality}:{localDate}";

    /// <summary>One measurement per local date at a time.</summary>
    public static string MeasureInsightsKey(string localDate) => $"{MeasureInsights}:{localDate}";

    public static string DeliverWebhookKey(Guid delivery) => $"{DeliverWebhook}:{delivery}";

    /// <summary>One build per local date at a time.</summary>
    public static string BuildTracksKey(string localDate) => $"{BuildTracks}:{localDate}";

    /// <summary>The build that follows the day's last <c>link-speakers</c> job; a key of its own, so a build already running never swallows it.</summary>
    public static string BuildTracksAfterLinksKey(string localDate) => $"{BuildTracks}:{localDate}:links";

    /// <summary>One job per scene, which a conversation's start identifies.</summary>
    public static string LinkSpeakersKey(Guid conversation, DateTime sceneStartedAt) =>
        $"{LinkSpeakers}:{conversation}:{sceneStartedAt.ToUniversalTime().Ticks}";
    /// <summary>One naming per local date at a time.</summary>
    public static string NameTracksKey(string localDate) => $"{NameTracks}:{localDate}";

    /// <summary>One build per local date at a time; the recut after the links has a key of its own, so a build already waiting never swallows it.</summary>
    public static string BuildSituationsKey(string localDate, bool recut = false) => $"{BuildSituations}:{localDate}" + (recut ? ":recut" : "");

    public static string EnrichSituationKey(Guid situation) => $"{EnrichSituation}:{situation}";

    /// <summary>One re-think per night at a time, the night being the local date on which the quiet hours began.</summary>
    public static string RethinkKey(string night) => $"{Rethink}:{night}";

    /// <summary>One recompute per local date at a time.</summary>
    public static string RecomputeEventsKey(string localDate) => $"{RecomputeEvents}:{localDate}";

    /// <summary>One scheduled job per local date; an on-demand run has a key of its own, so it never merges into a scheduled one.</summary>
    public static string MakeDigestKey(string localDate, bool replace = false) => $"{MakeDigest}:{localDate}" + (replace ? ":run" : "");

    public static JobLane LaneOf(string kind) => Claimed.GetValueOrDefault(kind, JobLane.Audio);

    /// <summary>Selects one lane's jobs in the queue: Ai and Hooks by their kinds, Audio by leaving those out.</summary>
    public static JobKindFilter FilterOf(JobLane lane) => lane == JobLane.Audio
        ? JobKindFilter.Except(Claimed.Keys)
        : JobKindFilter.Only(Claimed.Where(c => c.Value == lane).Select(c => c.Key));
}
