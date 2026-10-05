using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Server.Tags;
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
    TagStore tags,
    NpgsqlDataSource dataSource,
    ILlmClient llm,
    IOptionsMonitor<LlmOptions> options,
    SettingsService settings,
    IEventPublisher events,
    TimeProvider time,
    ILogger<EnrichConversationHandler> logger) : IJobHandler
{
    public const int MaxWindows = 6;

    public const int MinWords = 20;

    /// <summary>Failed rounds (of three attempts each) after which the scan stops retrying.</summary>
    public const int MaxFailedRounds = 3;

    /// <summary>
    /// Below this many words (about 25 seconds of speech) a conversation has no room for a task or a lasting
    /// fact: it gets a title and a one-sentence summary, and no task or memory run. Three times
    /// <see cref="MinWords"/>, under which nothing is summarized at all.
    /// </summary>
    public const int BriefWords = 60;

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
        if (conversation.AiStatus == "done" && conversation.AiThroughSegmentId == through)
        {
            return JobOutcome.Done; // a repeated job: these segments are summarized already
        }

        if (segments.Sum(s => Words(s.Text)) < MinWords)
        {
            // False: the conversation changed since the read (or is gone, which the next run finds out).
            return await conversations.SkipEnrichmentAsync(conversationId, through, segments.Count, TooShort, time.GetUtcNow(), ct)
                ? JobOutcome.Done
                : JobOutcome.RunAgain(Wait);
        }

        var zone = UserTimeZone.Resolve(settings);
        var lines = TranscriptText.Render(segments.Select(s => new TranscriptSegment(new DateTimeOffset(s.StartedAt), s.Label(), s.Text)), zone);
        var people = segments
            .Where(s => s.IsUser != true && s.PersonId is not null)
            .Select(s => new TaskPerson(s.PersonId!.Value, s.PersonName!))
            .DistinctBy(p => p.Id)
            .ToList();
        var brief = IsBrief(segments.Sum(s => Words(s.Text)));
        var suggestTags = TagSettings.Suggest(settings);
        ConversationAnswer answer;
        try
        {
            // Up to 100 tag names go to the model, so none are read when proposals are off or the conversation is brief.
            var tagNames = suggestTags && !brief ? await tags.NamesInUseAsync(ConversationPrompt.MaxTagNames, ct) : [];
            answer = await AskAsync(
                new DateTimeOffset(conversation.StartedAt), lines, options.CurrentValue, zone, brief,
                people.Select(p => p.Name).ToList(), tagNames, suggestTags, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A failure over a transcript that changed since the read (a merge, a late batch) must not
            // overwrite the state that change set: read again instead.
            if (!await conversations.EnrichmentReadIsCurrentAsync(conversationId, through, segments.Count, ct))
            {
                return JobOutcome.RunAgain(Wait);
            }

            if (error is LlmException llmError && IsPermanent(llmError))
            {
                // A 400, 401, 404 and the like will not pass on a second try: fail the round at once.
                await OnGiveUpAsync(job, error, ct);
                return JobOutcome.Done;
            }

            throw;
        }

        switch (await StoreAsync(conversationId, answer, people, through, segments.Count, ct))
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
        DateTimeOffset startedAt, IReadOnlyList<string> lines, LlmOptions llmOptions, TimeZoneInfo zone, bool brief,
        IReadOnlyList<string> people, IReadOnlyList<string> tagNames, bool suggestTags, CancellationToken ct)
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

        var zoneName = UserTimeZone.Name(zone);
        var system = ConversationPrompt.System(llmOptions.OutputLanguage, zoneName, brief, suggestTags);
        var parts = new List<ConversationAnswer>();
        for (var i = 0; i < windows.Count; i++)
        {
            parts.Add(await CompleteAsync(system, ConversationPrompt.User(startedAt, windows[i], i + 1, windows.Count, zone, people, tagNames), ct));
        }

        var answer = parts.Count == 1
            ? parts[0]
            : await CompleteAsync(
                ConversationPrompt.SystemForMerge(llmOptions.OutputLanguage, zoneName, suggestTags),
                ConversationPrompt.UserForMerge(startedAt, parts, zone, people, tagNames), ct);
        var result = brief ? answer with { Tasks = [] } : answer;
        return brief || !suggestTags ? result with { Tags = [] } : result;
    }

    private async Task<ConversationAnswer> CompleteAsync(string system, string user, CancellationToken ct) =>
        LlmJson.Parse<ConversationAnswer>(
            await llm.CompleteJsonAsync(new LlmRequest(ConversationPrompt.SchemaName, ConversationPrompt.Schema, system, user), ct));

    /// <summary>Stores the result, reconciles the tasks and publishes the events, in one transaction.</summary>
    private async Task<EnrichmentStore> StoreAsync(
        Guid conversationId, ConversationAnswer answer, IReadOnlyList<TaskPerson> people, long? through, int segmentCount,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var title = ConversationPrompt.Cut(answer.Title, ConversationPrompt.MaxTitle);
        var summary = ConversationPrompt.Cut(answer.Summary, ConversationPrompt.MaxSummary);
        var aiTasks = answer.Tasks
            .Select(t => (Text: ConversationPrompt.Cut(t.Text, ConversationPrompt.MaxTask), t.Person))
            .Select(t => new AiTask(t.Text, TextFingerprint.Of(t.Text), PersonNamed(people, t.Person)))
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
        await TagSuggestionStore.AddAsync(
            connection, transaction, conversationId, null, await ProposedTagsAsync(connection, transaction, conversationId, answer.Tags, people, ct), now, ct);
        await events.PublishAsync(new NytkaEvent(NytkaEvent.ConversationReady, conversationId), connection, transaction, ct);
        foreach (var taskId in created)
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.TaskCreated, taskId), connection, transaction, ct);
        }

        await transaction.CommitAsync(ct);
        return stored;
    }

    /// <summary>
    /// The model's tags that may be proposed: valid ones, not held by the conversation and not a listed person's name or a word of
    /// it, at most <see cref="ConversationPrompt.MaxTags"/>. A proposal for a name stored before, rejected ones included, is
    /// dropped by the insert.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ProposedTagsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, IReadOnlyList<string> proposed,
        IReadOnlyList<TaskPerson> people, CancellationToken ct)
    {
        var names = proposed.Select(TagName.Normalize).OfType<string>().Distinct().ToList();
        if (names.Count == 0)
        {
            return [];
        }

        var held = (await TagStore.OfConversationsAsync(connection, transaction, [conversationId], ct)).GetValueOrDefault(conversationId) ?? [];
        var persons = people
            .SelectMany(p => p.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Append(p.Name))
            .Select(TagName.Normalize)
            .OfType<string>()
            .ToHashSet();
        return names.Where(n => !held.Contains(n) && !persons.Contains(n)).Take(ConversationPrompt.MaxTags).ToList();
    }

    /// <summary>The listed person a name equals, ignoring case; null for no name or an unlisted one.</summary>
    private static Guid? PersonNamed(IReadOnlyList<TaskPerson> people, string? name) =>
        name is null ? null : people.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

    private sealed record TaskPerson(Guid Id, string Name);

    /// <summary>A client error other than 429: retrying with the same request and key cannot help.</summary>
    private static bool IsPermanent(LlmException error) => error.StatusCode is >= 400 and < 500 and not 429;

    public static bool IsBrief(int words) => words < BriefWords;

    public static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static EnrichPayload Payload(JobRecord job) => JsonSerializer.Deserialize<EnrichPayload>(job.Payload)!;
}
