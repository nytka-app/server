using System.Text.Json;
using Dapper;
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

    /// <summary>The text that stands for a run of media lines in the prompt.</summary>
    public const string MediaPlaying = "(media playing)";

    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(1);

    public string Kind => JobKinds.EnrichConversation;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = Payload(job);
        var conversationId = payload.ConversationId;
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
        if (!payload.Again && conversation.AiStatus == "done" && conversation.AiThroughSegmentId == through)
        {
            return JobOutcome.Done; // a repeated job: these segments are summarized already
        }

        if (SpokenWords(segments) < MinWords)
        {
            // False: the conversation changed since the read (or is gone, which the next run finds out).
            return await conversations.SkipEnrichmentAsync(conversationId, through, segments.Count, TooShort, time.GetUtcNow(), ct)
                ? JobOutcome.Done
                : JobOutcome.RunAgain(Wait);
        }

        var zone = UserTimeZone.Resolve(settings);
        var lines = TranscriptText.Render(WithoutMediaText(segments), zone);
        var people = segments
            .Where(s => s.IsUser != true && s.PersonId is not null)
            .Select(s => new TaskPerson(s.PersonId!.Value, s.PersonName!))
            .DistinctBy(p => p.Id)
            .ToList();
        var brief = IsBrief(SpokenWords(segments));
        var mediaLines = segments.Any(s => s.SpeechKind == SpeechKinds.Media && Words(s.Text) > 0);
        var suggestTags = TagSettings.Suggest(settings) && MediaShare(segments) < SpeechKinds.MediaShareLimit;
        ConversationAnswer answer;
        var template = await ChooseTemplateAsync(conversationId, brief, ct);
        try
        {
            // Up to 100 tag names go to the model, so none are read when proposals are off or the conversation is brief.
            var tagNames = suggestTags && !brief ? await tags.NamesInUseAsync(ConversationPrompt.MaxTagNames, ct) : [];
            answer = await AskAsync(
                new DateTimeOffset(conversation.StartedAt), lines, options.CurrentValue, zone, brief,
                people.Select(p => p.Name).ToList(), tagNames, suggestTags, mediaLines, template,
                AsksTopicBoundaries(conversation.StartedAt, conversation.EndedAt, brief), ct);
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

        switch (await StoreAsync(
                conversationId, answer, people, through, segments.Count, template.Recorded(answer.ConversationKind),
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(new DateTimeOffset(conversation.StartedAt), zone).DateTime), payload.Again, ct))
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
        IReadOnlyList<string> people, IReadOnlyList<string> tagNames, bool suggestTags, bool mediaLines, TemplateChoice template,
        bool topicBoundaries, CancellationToken ct)
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
        var system = ConversationPrompt.System(llmOptions.OutputLanguage, zoneName, brief, suggestTags, mediaLines, template.Spec, template.Kinds);
        var schema = template.Kinds is null ? ConversationPrompt.Schema : ConversationPrompt.SchemaWithKind;
        if (topicBoundaries)
        {
            schema = ConversationPrompt.WithTopicBoundaries(schema);
            system += ConversationPrompt.TopicBoundaryLine;
        }

        var parts = new List<ConversationAnswer>();
        for (var i = 0; i < windows.Count; i++)
        {
            parts.Add(await CompleteAsync(schema, system, ConversationPrompt.User(startedAt, windows[i], i + 1, windows.Count, zone, people, tagNames), ct));
        }

        var answer = parts.Count == 1
            ? parts[0]
            : await CompleteAsync(
                schema, ConversationPrompt.SystemForMerge(llmOptions.OutputLanguage, zoneName, suggestTags, mediaLines, template.Spec, template.Kinds)
                    + (topicBoundaries ? ConversationPrompt.TopicBoundaryLine + " Keep the boundaries of every part." : ""),
                ConversationPrompt.UserForMerge(startedAt, parts, zone, people, tagNames), ct);
        var result = brief ? answer with { Items = [] } : answer;
        result = topicBoundaries ? result : result with { TopicBoundaries = null };
        return brief || !suggestTags ? result with { Tags = [] } : result;
    }

    private async Task<ConversationAnswer> CompleteAsync(string schema, string system, string user, CancellationToken ct) =>
        LlmJson.Parse<ConversationAnswer>(
            await llm.CompleteJsonAsync(new LlmRequest(ConversationPrompt.SchemaName, schema, system, user), ct));

    /// <summary>
    /// The summary template for this run, read from the settings as they are now. A brief conversation takes none. With templates
    /// off, the default template applies (the prompt's own wording until it is edited). With them on, a tag of the conversation named
    /// after a template wins; otherwise the model names the kind in the same call and the template of that kind applies.
    /// </summary>
    private async Task<TemplateChoice> ChooseTemplateAsync(Guid conversationId, bool brief, CancellationToken ct)
    {
        if (brief)
        {
            return TemplateChoice.None;
        }

        try
        {
            if (!SummaryTemplates.Enabled(settings))
            {
                return new TemplateChoice(SummaryTemplates.Default, SummaryTemplates.Text(settings, SummaryTemplates.Default), null);
            }

            if (SummaryTemplates.FromTags(await tags.OfConversationAsync(conversationId, ct)) is { } tagged)
            {
                return new TemplateChoice(tagged, SummaryTemplates.Text(settings, tagged), null);
            }

            return new TemplateChoice(
                null, null, SummaryTemplates.Ids.ToDictionary(id => id, id => SummaryTemplates.Text(settings, id)));
        }
        catch (InvalidOperationException)
        {
            // An environment value that breaks a template's rules: the summary still gets made, from the built-in default.
            logger.LogWarning("A summary template setting from the environment is not valid; using the built-in default template.");
            return new TemplateChoice(SummaryTemplates.Default, SummaryTemplates.BuiltIn(SummaryTemplates.Default), null);
        }
    }

    /// <summary>
    /// <paramref name="Id"/> is the template when it is known before the call (null when the model picks); <paramref name="Spec"/> its text;
    /// <paramref name="Kinds"/> every template's text when the model picks. All three are null for a brief conversation.
    /// </summary>
    private sealed record TemplateChoice(string? Id, string? Spec, IReadOnlyDictionary<string, string>? Kinds)
    {
        public static readonly TemplateChoice None = new(null, null, null);

        /// <summary>The template that wrote the summary: the chosen one, else the model's kind when it is a template's, else the default; null for none.</summary>
        public string? Recorded(string? modelKind) =>
            Id ?? (Kinds is null ? null : SummaryTemplates.Kinds.Contains(modelKind) ? modelKind : SummaryTemplates.Default);
    }

    /// <summary>Stores the result, reconciles the tasks and notes, records what was dropped and publishes the events, in one transaction.</summary>
    private async Task<EnrichmentStore> StoreAsync(
        Guid conversationId, ConversationAnswer answer, IReadOnlyList<TaskPerson> people, long? through, int segmentCount,
        string? summaryTemplate, DateOnly conversationDate, bool again, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var title = ConversationPrompt.Cut(answer.Title, ConversationPrompt.MaxTitle);
        var summary = ConversationPrompt.Cut(answer.Summary, ConversationPrompt.MaxSummary);
        var classified = ItemClassifier.Classify(answer.Items, name => PersonNamed(people, name), conversationDate);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var stored = await conversations.StoreEnrichmentAsync(
            connection, transaction, conversationId, title.Length == 0 ? null : title, summary.Length == 0 ? null : summary,
            through, segmentCount, summaryTemplate, now, ct, again, Sensitivity.Parse(answer.Sensitivity));
        if (stored != EnrichmentStore.Stored)
        {
            return stored; // no event: the conversation is gone, changed since the read, or was summarized already
        }

        // The lines where a new topic starts, kept only when they are lines of this conversation (docs/explanation/features.md#scenes).
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update conversations
            set topic_boundaries = array(select s.id from segments s where s.conversation_id = @conversationId and s.id = any(@ids) order by s.id)
            where id = @conversationId
            """,
            new { conversationId, ids = (answer.TopicBoundaries ?? []).Distinct().ToArray() }, transaction, cancellationToken: ct));
        var created = await tasks.ReconcileAsync(connection, transaction, conversationId, classified.Tasks, now, ct);
        await NoteStore.ReplaceAsync(connection, transaction, conversationId, classified.Notes, now, ct);
        await NoteStore.ReplaceDroppedAsync(connection, transaction, conversationId, classified.Dropped, now, ct);
        await TagAsync(connection, transaction, conversationId, await ProposedTagsAsync(connection, transaction, conversationId, answer.Tags, people, ct), now, ct);
        await events.PublishAsync(new NytkaEvent(NytkaEvent.ConversationReady, conversationId), connection, transaction, ct);
        foreach (var taskId in created)
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.TaskCreated, taskId), connection, transaction, ct);
        }

        await transaction.CommitAsync(ct);
        logger.LogInformation(
            "Conversation {ConversationId} items: {Tasks} tasks, {Notes} notes, {Dropped} dropped.",
            conversationId, classified.Tasks.Count, classified.Notes.Count, classified.Dropped.Count);
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

    /// <summary>
    /// With <c>tags.auto</c> each proposed name is applied at once as the tag it is an alias of, or as itself, unless the
    /// conversation had a proposal of that name before (one the owner rejected or removed included); otherwise it waits as a proposal.
    /// </summary>
    private async Task TagAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, IReadOnlyList<string> names, DateTimeOffset now,
        CancellationToken ct)
    {
        if (!TagSettings.Auto(settings))
        {
            await TagSuggestionStore.AddAsync(connection, transaction, conversationId, null, names, now, ct);
            return;
        }

        var aliases = await TagStore.ResolveAsync(connection, transaction, names, ct);
        foreach (var name in names)
        {
            if (await TagSuggestionStore.AddAppliedAsync(connection, transaction, conversationId, name, now, ct))
            {
                await TagStore.AddToConversationAsync(connection, transaction, conversationId, aliases.GetValueOrDefault(name, name), now, ct, auto: true);
            }
        }
    }

    /// <summary>The listed person a name equals, ignoring case; null for no name or an unlisted one.</summary>
    private static Guid? PersonNamed(IReadOnlyList<TaskPerson> people, string? name) =>
        name is null ? null : people.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

    private sealed record TaskPerson(Guid Id, string Name);

    /// <summary>A client error other than 429: retrying with the same request and key cannot help.</summary>
    private static bool IsPermanent(LlmException error) => error.StatusCode is >= 400 and < 500 and not 429;

    public static bool IsBrief(int words) => words < BriefWords;

    /// <summary>Whether the call asks for topic boundaries: a conversation, not a brief one, over <see cref="ConversationPrompt.TopicBoundaryMinutes"/>.</summary>
    public static bool AsksTopicBoundaries(DateTime startedAt, DateTime endedAt, bool brief) =>
        !brief && endedAt - startedAt > TimeSpan.FromMinutes(ConversationPrompt.TopicBoundaryMinutes);

    /// <summary>The words of the lines that are not media: a conversation that is only a TV playing is too short to summarize.</summary>
    public static int SpokenWords(IEnumerable<SegmentRow> segments) =>
        segments.Where(s => s.SpeechKind != SpeechKinds.Media).Sum(s => Words(s.Text));

    /// <summary>The share of speech time whose kind is media, as <see cref="SpeechKinds.MediaShareJoin"/> computes it; 0 with no speech.</summary>
    public static double MediaShare(IReadOnlyList<SegmentRow> segments)
    {
        var total = segments.Sum(s => (s.EndedAt - s.StartedAt).TotalSeconds);
        return total <= 0 ? 0 : segments.Where(s => s.SpeechKind == SpeechKinds.Media).Sum(s => (s.EndedAt - s.StartedAt).TotalSeconds) / total;
    }

    /// <summary>
    /// The transcript as the model reads it: each run of media lines becomes one <c>Media</c> line without their words, so song lyrics
    /// or a film's dialogue never reach a summary, a task or a tag, while the summary may still say that media was playing.
    /// </summary>
    public static IEnumerable<TranscriptSegment> WithoutMediaText(IReadOnlyList<SegmentRow> segments)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.SpeechKind != SpeechKinds.Media)
            {
                yield return new TranscriptSegment(new DateTimeOffset(s.StartedAt), s.Label(), s.Text, s.Id);
                continue;
            }

            while (i + 1 < segments.Count && segments[i + 1].SpeechKind == SpeechKinds.Media)
            {
                i++;
            }

            yield return new TranscriptSegment(new DateTimeOffset(s.StartedAt), s.Label(), MediaPlaying, s.Id);
        }
    }

    public static int Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static EnrichPayload Payload(JobRecord job) => JsonSerializer.Deserialize<EnrichPayload>(job.Payload)!;
}
