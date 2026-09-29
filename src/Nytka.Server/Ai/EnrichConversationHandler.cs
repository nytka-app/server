using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;

namespace Nytka.Server.Ai;

/// <summary>
/// Gives a closed conversation a title, a summary and tasks (docs/specs/v0.2.md, "The AI layer").
/// The job runner supplies the three attempts and their backoff: a failure throws, and after the
/// last one <see cref="OnGiveUpAsync"/> records <c>failed</c>. The scheduler's scan brings a failed
/// conversation back an hour later.
/// </summary>
public sealed class EnrichConversationHandler(
    ConversationStore conversations,
    BatchStore batches,
    TaskStore tasks,
    NpgsqlDataSource dataSource,
    ILlmClient llm,
    IOptionsMonitor<LlmOptions> options,
    IEventPublisher events,
    TimeProvider time,
    ILogger<EnrichConversationHandler> logger) : IJobHandler
{
    public const int MaxWindows = 6;

    public const int MinWords = 20;

    /// <summary>Failed rounds (of three attempts each) after which the scan stops retrying.</summary>
    public const int MaxFailedRounds = 3;

    public const string TooShort = "Too short to summarize.";

    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(1);

    public string Kind => JobKinds.EnrichConversation;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var conversationId = Payload(job).ConversationId;
        if (await conversations.GetAsync(conversationId, ct) is not { } conversation)
        {
            return JobOutcome.Done;
        }

        // An open conversation is never summarized, and its transcript is still growing while a batch is pending.
        if (conversation.Status != "closed" || await batches.HasPendingAsync(conversationId, ct))
        {
            return JobOutcome.RunAgain(Wait);
        }

        var segments = await conversations.SegmentsAsync(conversationId, ct);
        var through = segments.Count == 0 ? (long?)null : segments.Max(s => s.Id);
        if (segments.Sum(s => Words(s.Text)) < MinWords)
        {
            // False: the conversation changed since the read (or is gone, which the next run finds out).
            return await conversations.SkipEnrichmentAsync(conversationId, through, segments.Count, TooShort, time.GetUtcNow(), ct)
                ? JobOutcome.Done
                : JobOutcome.RunAgain(Wait);
        }

        var lines = TranscriptText.Render(segments.Select(s => new TranscriptSegment(new DateTimeOffset(s.StartedAt), s.Speaker, s.Text)));
        ConversationAnswer answer;
        try
        {
            answer = await AskAsync(new DateTimeOffset(conversation.StartedAt), lines, options.CurrentValue, ct);
        }
        catch (LlmException error) when (IsPermanent(error))
        {
            // A 400, 401, 404 and the like will not pass on a second try: fail the round at once.
            await OnGiveUpAsync(job, error, ct);
            return JobOutcome.Done;
        }

        switch (await StoreAsync(conversationId, answer, through, segments.Count, ct))
        {
            case EnrichmentStore.Stale:
                // A late batch or a merge changed the conversation during the call: read it again once it is closed.
                logger.LogInformation("Conversation {ConversationId} changed during its summary; running again.", conversationId);
                return JobOutcome.RunAgain(Wait);
            case EnrichmentStore.Stored:
                logger.LogInformation("Conversation {ConversationId} summarized from {Segments} segments.", conversationId, segments.Count);
                break;
        }

        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) =>
        conversations.FailEnrichmentAsync(
            Payload(job).ConversationId,
            error is LlmException ? error.Message : "The summary could not be made.",
            time.GetUtcNow(),
            ct);

    /// <summary>One call for a transcript that fits a window; one per window and a merge call for a longer one.</summary>
    private async Task<ConversationAnswer> AskAsync(
        DateTimeOffset startedAt, IReadOnlyList<string> lines, LlmOptions llmOptions, CancellationToken ct)
    {
        if (!llm.IsConfigured)
        {
            throw new LlmException("The language model is not configured.");
        }

        var windows = TranscriptWindows.Split(lines, llmOptions.MaxInputChars);
        if (windows.Count > MaxWindows)
        {
            throw new LlmException("The conversation is too long to summarize.");
        }

        var system = ConversationPrompt.System(llmOptions.OutputLanguage);
        var parts = new List<ConversationAnswer>();
        for (var i = 0; i < windows.Count; i++)
        {
            parts.Add(await CompleteAsync(system, ConversationPrompt.User(startedAt, windows[i], i + 1, windows.Count), ct));
        }

        return parts.Count == 1
            ? parts[0]
            : await CompleteAsync(
                ConversationPrompt.SystemForMerge(llmOptions.OutputLanguage), ConversationPrompt.UserForMerge(startedAt, parts), ct);
    }

    private async Task<ConversationAnswer> CompleteAsync(string system, string user, CancellationToken ct) =>
        LlmJson.Parse<ConversationAnswer>(
            await llm.CompleteJsonAsync(new LlmRequest(ConversationPrompt.SchemaName, ConversationPrompt.Schema, system, user), ct));

    /// <summary>Stores the result, reconciles the tasks and publishes the events, in one transaction.</summary>
    private async Task<EnrichmentStore> StoreAsync(
        Guid conversationId, ConversationAnswer answer, long? through, int segmentCount, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var title = ConversationPrompt.Cut(answer.Title, ConversationPrompt.MaxTitle);
        var summary = ConversationPrompt.Cut(answer.Summary, ConversationPrompt.MaxSummary);
        var aiTasks = answer.Tasks
            .Select(t => ConversationPrompt.Cut(t, ConversationPrompt.MaxTask))
            .Select(t => new AiTask(t, TextFingerprint.Of(t)))
            .Where(t => t.Fingerprint.Length > 0)
            .DistinctBy(t => t.Fingerprint)
            .Take(ConversationPrompt.MaxTasks)
            .ToList();

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var stored = await conversations.StoreEnrichmentAsync(
            connection, transaction, conversationId, title.Length == 0 ? null : title, summary.Length == 0 ? null : summary,
            through, segmentCount, now, ct);
        if (stored != EnrichmentStore.Stored)
        {
            return stored; // no event: the conversation is gone, changed since the read, or was summarized already
        }

        var created = await tasks.ReconcileAsync(connection, transaction, conversationId, aiTasks, now, ct);
        await events.PublishAsync(new NytkaEvent(NytkaEvent.ConversationReady, conversationId), connection, transaction, ct);
        foreach (var taskId in created)
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.TaskCreated, taskId), connection, transaction, ct);
        }

        await transaction.CommitAsync(ct);
        return stored;
    }

    /// <summary>A client error other than 429: retrying with the same request and key cannot help.</summary>
    private static bool IsPermanent(LlmException error) => error.StatusCode is >= 400 and < 500 and not 429;

    private static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static EnrichPayload Payload(JobRecord job) => JsonSerializer.Deserialize<EnrichPayload>(job.Payload)!;
}
