namespace Nytka.Server.Jobs;

public static class JobKinds
{
    public const string ProcessSession = "process-session";
    public const string Transcribe = "transcribe";
    public const string CloseConversations = "close-conversations";
    public const string Retention = "retention";

    public static string ProcessSessionKey(Guid session) => $"{ProcessSession}:{session}";

    public static string TranscribeKey(long batchId) => $"{Transcribe}:{batchId}";
}
