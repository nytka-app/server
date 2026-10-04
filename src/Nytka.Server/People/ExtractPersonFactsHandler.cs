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
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>
/// The <c>extract-person-facts</c> job (docs/specs/people.md, Layer 3): asks the model for lasting facts about the
/// people in one conversation and applies them in one transaction. The server sets each fact's basis from the
/// evidence segment and drops what the evidence does not support. The job runner makes the three attempts; on the
/// third the failure is recorded here and the job comes back an hour later, three rounds at most.
/// </summary>
public sealed class ExtractPersonFactsHandler(
    ILlmClient llm,
    IOptionsMonitor<LlmOptions> llmOptions,
    SettingsService settings,
    PersonFactStore facts,
    MemoryStore memories,
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
        { "type": "object", "additionalProperties": false, "required": ["facts"],
          "properties": { "facts": { "type": "array", "items": { "type": "object",
            "additionalProperties": false, "required": ["personId", "text", "segmentId"],
            "properties": { "personId": { "type": "string" }, "text": { "type": "string" }, "segmentId": { "type": "integer" } } } } } }
        """;

    public string Kind => JobKinds.ExtractPersonFacts;

    public sealed record Candidate(string PersonId, string Text, long SegmentId);

    public sealed record Answer(IReadOnlyList<Candidate> Facts);

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
            var candidates = involved.Count == 0 ? [] : await AskAsync(input, involved, ct);
            await ApplyAsync(conversationId, candidates, covered ? run?.ThroughSegmentId : input.LastSegmentId, ct);
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

    /// <summary>What the model proposes for each window of the transcript, kept only with a basis, pooled and cut to ten.</summary>
    private async Task<IReadOnlyList<FactCandidate>> AskAsync(FactInput input, IReadOnlyList<PersonRef> involved, CancellationToken ct)
    {
        var zone = UserTimeZone.Resolve(settings);
        var lines = input.Segments.Select(s => new { s.Id, Line = TranscriptText.Render(
                [new TranscriptSegment(new DateTimeOffset(s.StartedAt, TimeSpan.Zero), s.Speaker, s.Text)], zone).FirstOrDefault() })
            .Where(l => l.Line is not null)
            .Select(l => $"#{l.Id} {l.Line}")
            .ToList();
        var windows = TranscriptWindows.Split(lines, llmOptions.CurrentValue.MaxInputChars);
        var known = await facts.KnownAsync(involved.Select(p => p.Id).ToList(), ct);
        var system = SystemMessage(llmOptions.CurrentValue.OutputLanguage, UserTimeZone.Name(zone));
        var userName = MemorySettings.UserName(settings);
        var segments = input.Segments.ToDictionary(s => s.Id);
        var people = involved.ToDictionary(p => p.Id);

        var pooled = new List<FactCandidate>();
        foreach (var window in windows)
        {
            var answer = LlmJson.Parse<Answer>(await llm.CompleteJsonAsync(
                new LlmRequest(SchemaName, Schema, system, UserMessage(input, userName, involved, known, window, zone)), ct));
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
        }

        return pooled.Take(MaxFactsPerRun).ToList();
    }

    private async Task ApplyAsync(Guid conversationId, IReadOnlyList<FactCandidate> candidates, long? through, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // The conversation may have been deleted while the model worked; its facts go with it.
        if (!await memories.LockConversationAsync(connection, transaction, conversationId, ct))
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var id in await facts.ApplyAsync(connection, transaction, conversationId, candidates, now, ct))
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.PersonFactCreated, id), connection, transaction, ct);
        }

        await facts.MarkDoneAsync(connection, transaction, conversationId, through, now, ct);
        await transaction.CommitAsync(ct);
    }

    public static string SystemMessage(string outputLanguage, string timeZone = UserTimeZone.Default) =>
        Prompt(string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase) ? "the language of the conversation" : outputLanguage, timeZone);

    private static string Prompt(string outputLanguage, string timeZone) =>
        $"""
        You read a transcript of a conversation and pick out lasting facts about the people listed in the user message.
        A lasting fact is about one listed person and stays true beyond this conversation: who they are, their family, home, work, health, habits, preferences, goals and commitments.
        Take a fact only from a line that states it, and give that line's segment id. Never report facts about the person labelled "Wearer", about anyone not listed, one-off events, tasks, or plans for a single day.
        A line may name a listed person while another speaker, or the wearer, is talking: a fact stated there counts for the person named. Do not guess who a line is about.
        Audio from a TV, video, podcast, radio, song or game playing nearby, and text read aloud from a script or screen, is not a person's life: take no facts from it. Take no facts inferred from tone, manner of speech or vocabulary.
        Times and dates are in the time zone {timeZone}. Write dates in a fact as absolute dates, never as "tomorrow" or "Friday".
        Write each fact as one short sentence, at most {ExtractMemoriesHandler.MaxTextLength} characters, in {outputLanguage}.
        Return at most {MaxFactsPerRun} facts, none that a known fact already states. Return an empty list when there is nothing lasting.
        """;

    public static string UserMessage(
        FactInput input, string? userName, IReadOnlyList<PersonRef> involved, IReadOnlyList<KnownFact> known, string transcript, TimeZoneInfo? zone = null)
    {
        var message = new StringBuilder();
        message.Append("Conversation: ").Append(string.IsNullOrWhiteSpace(input.Title) ? "(untitled)" : input.Title).Append('\n');
        message.Append("Date: ").Append(ConversationPrompt.LocalDate(new DateTimeOffset(input.StartedAt, TimeSpan.Zero), zone)).Append('\n');
        message.Append("\"Wearer\" is ").Append(userName ?? "the person wearing the pendant").Append(".\n\n");
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
