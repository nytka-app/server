using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Memories;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Server.Tags;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>
/// The <c>extract-person-facts</c> job (docs/specs/people.md, Layer 3): asks the model for lasting facts about the
/// people in one conversation and applies them in one transaction. The server sets each fact's basis from the
/// evidence segment and drops what the evidence does not support. The job runner makes the three attempts; on the
/// third the failure is recorded here and the job comes back an hour later, three rounds at most. The same answer may
/// propose tags for the listed people (docs/specs/tags.md): they are stored as proposals in the facts' transaction and link
/// nothing until the owner accepts one.
/// </summary>
public sealed class ExtractPersonFactsHandler(
    ILlmClient llm,
    IOptionsMonitor<LlmOptions> llmOptions,
    SettingsService settings,
    PersonFactStore facts,
    MemoryStore memories,
    TagStore tags,
    NpgsqlDataSource dataSource,
    IEventPublisher events,
    TimeProvider time,
    ILogger<ExtractPersonFactsHandler> logger)
    : IJobHandler
{
    public const int MaxFactsPerRun = 10;
    public const int MaxRounds = 3;
    public const string SchemaName = "person_facts";

    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["facts", "tags"],
          "properties": {
            "facts": { "type": "array", "items": { "type": "object",
              "additionalProperties": false, "required": ["personId", "text", "segmentId"],
              "properties": { "personId": { "type": "string" }, "text": { "type": "string" }, "segmentId": { "type": "integer" } } } },
            "tags": { "type": "array", "items": { "type": "object",
              "additionalProperties": false, "required": ["personId", "tag"],
              "properties": { "personId": { "type": "string" }, "tag": { "type": "string" } } } } } }
        """;

    public string Kind => JobKinds.ExtractPersonFacts;

    public sealed record Candidate(string PersonId, string Text, long SegmentId);

    public sealed record TagCandidate(string PersonId, string Tag);

    public sealed record Answer(IReadOnlyList<Candidate> Facts, IReadOnlyList<TagCandidate> Tags);

    /// <summary>What a run keeps from the model: facts with their basis, and valid tags for people that were sent.</summary>
    private sealed record Asked(IReadOnlyList<FactCandidate> Facts, IReadOnlyList<(Guid PersonId, string Tag)> Tags);

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var conversationId = JsonSerializer.Deserialize<ExtractPayload>(job.Payload)!.ConversationId;
        if (!PeopleSettings.FactsEnabled(settings) || !llm.IsConfigured)
        {
            await facts.ForgetRunAsync(conversationId, ct);
            return JobOutcome.Done;
        }

        if (await facts.ReadInputAsync(conversationId, ct) is not { } input)
        {
            return JobOutcome.Done;
        }

        try
        {
            var run = await facts.GetRunAsync(conversationId, ct);
            var covered = input.LastSegmentId is null || run?.ThroughSegmentId >= input.LastSegmentId;
            var brief = EnrichConversationHandler.IsBrief(input.Segments.Sum(s => EnrichConversationHandler.Words(s.Text)));
            var involved = covered || brief ? [] : FactBasis.Involved(input.Segments, await facts.PeopleAsync(ct));
            var asked = involved.Count == 0 ? new Asked([], []) : await AskAsync(input, involved, ct);
            await ApplyAsync(conversationId, asked, covered ? run?.ThroughSegmentId : input.LastSegmentId, ct);
            return JobOutcome.Done;
        }
        catch (Exception error) when (job.Attempts >= JobRunner.MaxAttempts && !ct.IsCancellationRequested)
        {
            // Only the third attempt records the failure; the earlier ones throw and the runner tries again.
            var message = Describe(error);
            logger.LogWarning(error, "Extracting person facts failed after {Attempts} attempts: {Message}", job.Attempts, message);
            var failures = await facts.MarkFailedAsync(conversationId, message, time.GetUtcNow(), ct);
            return failures is { } count && count < MaxRounds ? JobOutcome.RunAgain(RetryAfter) : JobOutcome.Done;
        }
    }

    /// <summary>The run's failure is recorded by <see cref="RunAsync"/> on its last attempt, so there is nothing left to do.</summary>
    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// What the model proposes for each window of the transcript: facts kept only with a basis, pooled and cut to ten, and tag
    /// names normalized, for people that were sent, pooled.
    /// </summary>
    private async Task<Asked> AskAsync(FactInput input, IReadOnlyList<PersonRef> involved, CancellationToken ct)
    {
        var zone = UserTimeZone.Resolve(settings);
        var lines = input.Segments.Where(s => !s.IsMedia).Select(s => new { s.Id, Line = TranscriptText.Render(
                [new TranscriptSegment(new DateTimeOffset(s.StartedAt, TimeSpan.Zero), s.Speaker, s.Text)], zone).FirstOrDefault() })
            .Where(l => l.Line is not null)
            .Select(l => $"#{l.Id} {l.Line}")
            .ToList();
        var windows = TranscriptWindows.Split(lines, llmOptions.CurrentValue.MaxInputChars);
        var known = await facts.KnownAsync(involved.Select(p => p.Id).ToList(), ct);
        var suggestTags = TagSettings.Suggest(settings);
        // Up to 100 tag names go to the model, so none are read when proposals are off.
        var tagNames = suggestTags ? await tags.NamesInUseAsync(ConversationPrompt.MaxTagNames, ct) : [];
        var system = SystemMessage(llmOptions.CurrentValue.OutputLanguage, UserTimeZone.Name(zone), suggestTags);
        var userName = MemorySettings.UserName(settings);
        var segments = input.Segments.ToDictionary(s => s.Id);
        var people = involved.ToDictionary(p => p.Id);

        var pooled = new List<FactCandidate>();
        var pooledTags = new List<(Guid PersonId, string Tag)>();
        var personNames = involved
            .SelectMany(p => p.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Append(p.Name))
            .Select(TagName.Normalize)
            .OfType<string>()
            .ToHashSet();
        foreach (var window in windows)
        {
            var answer = LlmJson.Parse<Answer>(await llm.CompleteJsonAsync(
                new LlmRequest(SchemaName, Schema, system, UserMessage(input, userName, involved, known, window, zone, tagNames)), ct));
            foreach (var candidate in answer.Facts)
            {
                var text = ExtractMemoriesHandler.Cut(candidate.Text);
                var fingerprint = TextFingerprint.Of(text);
                if (fingerprint.Length == 0
                    || !Guid.TryParse(candidate.PersonId, out var personId) || !people.TryGetValue(personId, out var person)
                    || !segments.TryGetValue(candidate.SegmentId, out var segment)
                    || FactBasis.Of(segment, personId, person.Name) is not { } basis
                    || pooled.Any(p => p.PersonId == personId && p.Fingerprint == fingerprint))
                {
                    continue;
                }

                pooled.Add(new FactCandidate(personId, text, fingerprint, basis, segment.Id));
            }

            foreach (var candidate in suggestTags ? answer.Tags : [])
            {
                if (Guid.TryParse(candidate.PersonId, out var personId) && people.ContainsKey(personId)
                    && TagName.Normalize(candidate.Tag) is { } tag && !personNames.Contains(tag) && !pooledTags.Contains((personId, tag)))
                {
                    pooledTags.Add((personId, tag));
                }
            }
        }

        return new Asked(pooled.Take(MaxFactsPerRun).ToList(), pooledTags);
    }

    private async Task ApplyAsync(Guid conversationId, Asked asked, long? through, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // The conversation may have been deleted while the model worked; its facts go with it.
        if (!await memories.LockConversationAsync(connection, transaction, conversationId, ct))
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var id in await facts.ApplyAsync(connection, transaction, conversationId, asked.Facts, now, ct))
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.PersonFactCreated, id), connection, transaction, ct);
        }

        await ProposeTagsAsync(connection, transaction, conversationId, asked.Tags, now, ct);
        await facts.MarkDoneAsync(connection, transaction, conversationId, through, now, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Stores the tags that may be proposed: for a person who still exists, not held by them, at most
    /// <see cref="ConversationPrompt.MaxTags"/> per person. A name stored before for that person, rejected ones included, is
    /// dropped by the insert.
    /// </summary>
    private async Task ProposeTagsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId,
        IReadOnlyList<(Guid PersonId, string Tag)> proposed, DateTimeOffset now, CancellationToken ct)
    {
        if (proposed.Count == 0)
        {
            return;
        }

        var held = await TagStore.OfPeopleAsync(connection, transaction, proposed.Select(t => t.PersonId).Distinct().ToList(), ct);
        foreach (var person in proposed.GroupBy(t => t.PersonId))
        {
            if (!await PersonFactStore.LockPersonAsync(connection, transaction, person.Key, ct))
            {
                continue;
            }

            var has = held.GetValueOrDefault(person.Key) ?? [];
            await TagSuggestionStore.AddAsync(
                connection, transaction, conversationId, person.Key,
                person.Select(t => t.Tag).Where(t => !has.Contains(t)).Take(ConversationPrompt.MaxTags), now, ct);
        }
    }

    /// <summary>
    /// <paramref name="suggestTags"/> is false when the owner turned tag proposals off: the schema still has <c>tags</c>, so the
    /// model is told to return none.
    /// </summary>
    public static string SystemMessage(string outputLanguage, string timeZone = UserTimeZone.Default, bool suggestTags = true) =>
        Prompt(string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase) ? "the language of the conversation" : outputLanguage, timeZone, suggestTags);

    private static string Prompt(string outputLanguage, string timeZone, bool suggestTags)
    {
        var tagRule = suggestTags
            ? $"""Tags: for a listed person, return at most {ConversationPrompt.MaxTags} short lowercase tags for who they are to the wearer or what they do (for example "neighbour", "colleague", "doctor"), or none when nothing fits. Take a tag only from what the transcript says. When the "Tags" line in the user message lists a tag that fits, use it as written. A tag is never a person's name, and never about age, gender, health, religion, ethnicity or politics, or about how someone sounds."""
            : "Tags: return an empty list.";
        return
            $"""
            You read a transcript of a conversation and pick out lasting facts about the people listed in the user message.
            A lasting fact is about one listed person and stays true beyond this conversation: who they are, their family, home, work, health, habits, preferences, goals and commitments.
            Take a fact only from a line that states it, and give that line's segment id. Never report facts about the person labelled "Wearer", about anyone not listed, one-off events, tasks, or plans for a single day.
            A line may name a listed person while another speaker, or the wearer, is talking: a fact stated there counts for the person named. Do not guess who a line is about.
            Audio from a TV, video, podcast, radio, song or game playing nearby, and text read aloud from a script or screen, is not a person's life: take no facts from it. Take no facts inferred from tone, manner of speech or vocabulary.
            Times and dates are in the time zone {timeZone}. Write dates in a fact as absolute dates, never as "tomorrow" or "Friday".
            Write each fact as one short sentence, at most {ExtractMemoriesHandler.MaxTextLength} characters, in {outputLanguage}.
            Return at most {MaxFactsPerRun} facts, none that a known fact already states. Return an empty list when there is nothing lasting.
            {tagRule}
            """;
    }

    public static string UserMessage(
        FactInput input, string? userName, IReadOnlyList<PersonRef> involved, IReadOnlyList<KnownFact> known, string transcript, TimeZoneInfo? zone = null,
        IReadOnlyList<string>? tagNames = null)
    {
        var message = new StringBuilder();
        message.Append("Conversation: ").Append(string.IsNullOrWhiteSpace(input.Title) ? "(untitled)" : input.Title).Append('\n');
        message.Append("Date: ").Append(ConversationPrompt.LocalDate(new DateTimeOffset(input.StartedAt, TimeSpan.Zero), zone)).Append('\n');
        message.Append("\"Wearer\" is ").Append(userName ?? "the person wearing the pendant").Append('.').Append(ConversationPrompt.TagsLine(tagNames)).Append("\n\n");
        message.Append("People (id: name), each with the facts already known:\n");
        foreach (var person in involved)
        {
            message.Append(person.Id).Append(": ").Append(person.Name).Append('\n');
            foreach (var fact in known.Where(k => k.PersonId == person.Id))
            {
                message.Append("  - ").Append(fact.Text).Append('\n');
            }
        }

        message.Append("\nTranscript (each line starts with its segment id):\n").Append(transcript);
        return message.ToString();
    }

    /// <summary>A short reason for <c>people_runs.message</c>: a status, never a response body or transcript text.</summary>
    private static string Describe(Exception error) => error switch
    {
        LlmException { StatusCode: { } code } => $"HTTP {code}",
        LlmException llm => llm.Message,
        OperationCanceledException or TimeoutException => "timeout",
        HttpRequestException => "connection refused",
        _ => "unexpected error",
    };
}
