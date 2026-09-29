using Nytka.Storage;

namespace Nytka.Server.Jobs;

public static class JobKinds
{
    public const string ProcessSession = "process-session";
    public const string Transcribe = "transcribe";
    public const string CloseConversations = "close-conversations";
    public const string Retention = "retention";
    public const string EnrichConversation = "enrich-conversation";
    public const string ExtractMemories = "extract-memories";
    public const string DeliverWebhook = "deliver-webhook";

    /// <summary>
    /// The kinds that run outside the Audio lane. Every other kind, v0.1's four and one nobody
    /// handles alike, runs in Audio, so an unknown kind still fails and gives up like any job.
    /// </summary>
    private static readonly Dictionary<string, JobLane> Claimed = new()
    {
        [EnrichConversation] = JobLane.Ai,
        [ExtractMemories] = JobLane.Ai,
        [DeliverWebhook] = JobLane.Hooks,
    };

    public static string ProcessSessionKey(Guid session) => $"{ProcessSession}:{session}";

    public static string TranscribeKey(long batchId) => $"{Transcribe}:{batchId}";

    public static string EnrichConversationKey(Guid conversation) => $"{EnrichConversation}:{conversation}";

    public static string ExtractMemoriesKey(Guid conversation) => $"{ExtractMemories}:{conversation}";

    public static string DeliverWebhookKey(Guid delivery) => $"{DeliverWebhook}:{delivery}";

    public static JobLane LaneOf(string kind) => Claimed.GetValueOrDefault(kind, JobLane.Audio);

    /// <summary>Selects one lane's jobs in the queue: Ai and Hooks by their kinds, Audio by leaving those out.</summary>
    public static JobKindFilter FilterOf(JobLane lane) => lane == JobLane.Audio
        ? JobKindFilter.Except(Claimed.Keys)
        : JobKindFilter.Only(Claimed.Where(c => c.Value == lane).Select(c => c.Key));
}
