namespace Nytka.Server.Events;

/// <summary>
/// Something that happened, for the rest of the server to react to. <paramref name="SubjectId"/> is
/// the id of the conversation, task, memory or bookmark it is about.
/// </summary>
public sealed record NytkaEvent(string Type, Guid SubjectId)
{
    /// <summary>A summary was stored; the subject is the conversation.</summary>
    public const string ConversationReady = "conversation.ready";

    /// <summary>The summary produced a new task; the subject is the task.</summary>
    public const string TaskCreated = "task.created";

    /// <summary>A task was completed; the subject is the task.</summary>
    public const string TaskCompleted = "task.completed";

    /// <summary>A memory was added; the subject is the memory.</summary>
    public const string MemoryCreated = "memory.created";

    /// <summary>A bookmark was added; the subject is the bookmark.</summary>
    public const string BookmarkCreated = "bookmark.created";

    /// <summary>A daily digest was stored; the subject is the digest.</summary>
    public const string DigestReady = "digest.ready";
}
