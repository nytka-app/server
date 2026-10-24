using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;
using Npgsql;

namespace Nytka.Server.Memories;

/// <summary>
/// The <c>extract-memories</c> job (docs/specs/v0.4.md, Memories): asks the model for lasting facts about the
/// user in one conversation and applies them in one transaction. The job runner makes the three attempts; on
/// the third the failure is recorded here and the job comes back an hour later, three rounds at most.
/// </summary>
public sealed class ExtractMemoriesHandler(
    ILlmClient llm,
    IOptionsMonitor<LlmOptions> llmOptions,
    SettingsService settings,
    MemoryStore memories,
    NpgsqlDataSource dataSource,
    IEventPublisher events,
    TimeProvider time,
    ILogger<ExtractMemoriesHandler> logger)
    : IJobHandler
{
    public const int MaxMemoriesPerRun = 5;
    public const int MaxTextLength = 300;
    public const int KnownMemories = 200;
    public const int MaxRounds = 3;
    public const string SchemaName = "memories";

    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["memories"],
          "properties": { "memories": { "type": "array", "items": { "type": "object",
            "additionalProperties": false, "required": ["text", "replaces", "salience", "expires", "segmentIds", "sensitivity"],
            "properties": { "text": { "type": "string" }, "replaces": { "type": ["string", "null"] },
                            "salience": { "type": "string", "enum": ["vital", "ordinary", "noise"] },
                            "expires": { "type": ["string", "null"] },
                            "segmentIds": { "type": "array", "items": { "type": "integer" } },
                            "sensitivity": { "type": "string", "enum": ["none", "health", "therapy", "intimate", "money"] } } } } } }
        """;

    public string Kind => JobKinds.ExtractMemories;

    /// <summary>The latest expiry a fact may take, counted from the conversation's date; a later date is dropped as a guess.</summary>
    public const int MaxExpiryDays = 730;

    /// <summary>A proposed fact. <paramref name="Salience"/> and <paramref name="Expires"/> are missing in answers from before they existed.</summary>
    public sealed record Candidate(
        string Text, string? Replaces, string? Salience = null, string? Expires = null, IReadOnlyList<long>? SegmentIds = null,
        string? Sensitivity = null);

    public sealed record Answer(IReadOnlyList<Candidate> Memories);

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ExtractPayload>(job.Payload)!;
        var conversationId = payload.ConversationId;
        if (!MemorySettings.IsEnabled(settings) || !llm.IsConfigured)
        {
            await memories.ForgetRunAsync(conversationId, ct);
            return JobOutcome.Done;
        }

        if (await memories.ReadInputAsync(conversationId, ct) is not { } input)
        {
            return JobOutcome.Done;
        }

        try
        {
            var run = await memories.GetRunAsync(conversationId, ct);
            var covered = input.LastSegmentId is null || (!payload.Again && run?.ThroughSegmentId >= input.LastSegmentId);
            // A conversation too short for tasks has no lasting facts either (see EnrichConversationHandler.BriefWords).
            var brief = EnrichConversationHandler.IsBrief(input.Segments.Sum(s => EnrichConversationHandler.Words(s.Text)));
            var candidates = covered || brief ? [] : await AskAsync(input, ct);
            await ApplyAsync(conversationId, candidates, covered ? run?.ThroughSegmentId : input.LastSegmentId, ct);
            return JobOutcome.Done;
        }
        catch (Exception error) when (job.Attempts >= JobRunner.MaxAttempts && !ct.IsCancellationRequested)
        {
            // Only the third attempt records the failure; the earlier ones throw and the runner tries again.
            var message = Describe(error);
            logger.LogWarning(error, "Extracting memories failed after {Attempts} attempts: {Message}", job.Attempts, message);
            var failures = await memories.MarkFailedAsync(conversationId, message, time.GetUtcNow(), ct);
            return failures is { } count && count < MaxRounds ? JobOutcome.RunAgain(RetryAfter) : JobOutcome.Done;
        }
    }

    /// <summary>The run's failure is recorded by <see cref="RunAsync"/> on its last attempt, so there is nothing left to do.</summary>
    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>What the model proposes for each window of the transcript, pooled, cut to length and kept to five.</summary>
    private async Task<IReadOnlyList<MemoryCandidate>> AskAsync(ExtractionInput input, CancellationToken ct)
    {
        var lines = TranscriptText.Render(input.Segments.Where(s => !s.IsMedia).Select(s =>
            new TranscriptSegment(new DateTimeOffset(s.StartedAt, TimeSpan.Zero), s.Speaker, s.Text, s.Id)), UserTimeZone.Resolve(settings));
        var windows = TranscriptWindows.Split(lines, llmOptions.CurrentValue.MaxInputChars);
        var known = await memories.NewestAsync(KnownMemories, ct);
        var listed = known.Select(k => k.Id).ToHashSet();
        var zone = UserTimeZone.Resolve(settings);
        var system = SystemMessage(llmOptions.CurrentValue.OutputLanguage, UserTimeZone.Name(zone));
        var userName = MemorySettings.UserName(settings);

        var from = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(new DateTimeOffset(input.StartedAt, TimeSpan.Zero), zone).DateTime);
        var pooled = new List<MemoryCandidate>();
        foreach (var window in windows)
        {
            var answer = LlmJson.Parse<Answer>(await llm.CompleteJsonAsync(
                new LlmRequest(SchemaName, Schema, system, UserMessage(input, userName, known, window, zone)), ct));
            foreach (var candidate in answer.Memories)
            {
                var text = Cut(candidate.Text);
                var fingerprint = TextFingerprint.Of(text);
                if (fingerprint.Length == 0)
                {
                    continue;
                }

                Guid? replaces = Guid.TryParse(candidate.Replaces, out var id) && listed.Contains(id) ? id : null;
                var cited = Cited(candidate.SegmentIds, input.Segments);
                pooled.Add(new MemoryCandidate(
                    text, fingerprint, replaces, Salience.Parse(candidate.Salience), ExpiresAt(candidate.Expires, from, zone),
                    cited, candidate.SegmentIds is null || IsGrounded(cited, input.Segments), Storage.Sensitivity.Parse(candidate.Sensitivity)));
            }
        }

        return pooled.Take(MaxMemoriesPerRun).ToList();
    }

    /// <summary>The cited ids that are lines of the transcript, each once; the model's other ids are dropped.</summary>
    public static IReadOnlyList<long> Cited(IReadOnlyList<long>? ids, IReadOnlyList<SegmentLine> segments)
    {
        var known = segments.Select(s => s.Id).ToHashSet();
        return [.. (ids ?? []).Where(known.Contains).Distinct()];
    }

    /// <summary>
    /// Whether a fact about "you" is grounded: one of its cited lines is the wearer's by the label rule (so at or above
    /// <c>voice.userThreshold</c> when the voice decided) and is no media. A fact from an unlabelled or another person's line alone
    /// is stored unconfirmed and never shown (docs/explanation/features.md, Memories). The schema makes the model cite; an answer
    /// without the field at all (from before it) is taken as before.
    /// </summary>
    public static bool IsGrounded(IReadOnlyList<long> cited, IReadOnlyList<SegmentLine> segments)
    {
        var wearer = segments.Where(s => !s.IsMedia && s.Speaker == SpeakerLabel.Wearer).Select(s => s.Id).ToHashSet();
        return cited.Any(wearer.Contains);
    }

    /// <summary>
    /// The instant a fact stops being true: the start of the day after <paramref name="text"/> (<c>YYYY-MM-DD</c>) in the owner's time zone.
    /// Null for a lasting fact, and for a date before the conversation's day or more than <see cref="MaxExpiryDays"/> after it.
    /// </summary>
    public static DateTimeOffset? ExpiresAt(string? text, DateOnly from, TimeZoneInfo zone) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && date >= from && date <= from.AddDays(MaxExpiryDays)
            ? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero)
            : null;

    private async Task ApplyAsync(Guid conversationId, IReadOnlyList<MemoryCandidate> candidates, long? through, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // The conversation may have been deleted while the model worked; its memories go with it.
        if (!await memories.LockConversationAsync(connection, transaction, conversationId, ct))
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var id in await memories.ApplyAsync(connection, transaction, conversationId, candidates, now, ct))
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.MemoryCreated, id), connection, transaction, ct);
        }

        await memories.MarkDoneAsync(connection, transaction, conversationId, through, now, ct);
        await transaction.CommitAsync(ct);
    }

    public static string SystemMessage(string outputLanguage, string timeZone = UserTimeZone.Default) =>
        Prompt(string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase) ? "the language of the conversation" : outputLanguage, timeZone);

    private static string Prompt(string outputLanguage, string timeZone) =>
        $"""
        You read a transcript of a conversation and pick out lasting facts about one person, called "you" below.
        A lasting fact is about you, stays true beyond this conversation, and comes from you saying it about yourself or from someone saying it about you and you confirming it: who you are, your family, friends, home, work, health, habits, preferences, goals and commitments.
        Never report facts about other speakers or third parties, one-off events, tasks, plans for a single day, or what someone else said they would do. A fact about another person counts only as your relation to them (for example, that a named person is your sister).
        Audio from a TV, video, podcast, radio, song or game playing nearby, and text you read aloud from a script or screen, is not your life: take no facts from it. Take no facts inferred from your tone, manner of speech or vocabulary (for example swearing or humour).
        Lines labelled "Wearer" are yours. Other speaker labels may differ between parts of the transcript; the user message says who "you" is. A line labelled "Unknown" has no known speaker and is never yours: never take a fact about you from it alone, however it is worded.
        Each line starts with its time and its segment id ("#48121"). Give each fact the ids (without "#") of the lines it comes from in segmentIds, your own line among them. A fact no line of yours states or confirms is not yours.
        Times and dates are in the time zone {timeZone}. Write dates in a fact as absolute dates, never as "tomorrow" or "Friday".
        Write each fact as one short sentence, at most {MaxTextLength} characters, in {outputLanguage}.
        Some facts are true only for a while: where you are staying, an illness, a job you are about to leave, a plan with a date. Set "expires" to the last date (YYYY-MM-DD) the fact is still true when the transcript says it or makes it plain, and to null for a lasting fact; never guess a date.
        Set "sensitivity" to "health", "therapy", "intimate" or "money" for a fact about your health, a therapy or counselling session, your sex life or the private side of a relationship, or your money; "none" otherwise.
        Set "salience" to "vital" for what shapes your life (family, health, home, work, money, a big decision), "noise" for trivia (a passing taste or opinion), and "ordinary" for the rest.
        Return at most {MaxMemoriesPerRun} facts, none that a known memory already states. When a fact updates a
        known memory, set "replaces" to that memory's id, otherwise to null. Return an empty list when there is
        nothing lasting.
        """;

    public static string UserMessage(ExtractionInput input, string? userName, IReadOnlyList<KnownMemory> known, string transcript, TimeZoneInfo? zone = null)
    {
        var message = new StringBuilder();
        message.Append("Conversation: ").Append(string.IsNullOrWhiteSpace(input.Title) ? "(untitled)" : input.Title).Append('\n');
        message.Append("Date: ").Append(ConversationPrompt.LocalDate(new DateTimeOffset(input.StartedAt, TimeSpan.Zero), zone)).Append('\n');
        message.Append("\"You\" is ").Append(userName ?? "the person wearing the pendant").Append(".\n\n");
        message.Append("Known memories (id: text):\n");
        message.Append(known.Count == 0 ? "(none)" : string.Join('\n', known.Select(k => $"{k.Id}: {k.Text}")));
        message.Append("\n\nTranscript:\n").Append(transcript);
        return message.ToString();
    }

    /// <summary>One line, at most <see cref="MaxTextLength"/> characters (Postgres counts code points, as runes do).</summary>
    public static string Cut(string text)
    {
        var line = OneLine(text);
        var index = 0;
        var count = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (count == MaxTextLength)
            {
                return line[..index].TrimEnd();
            }

            index += rune.Utf16SequenceLength;
            count++;
        }

        return line;
    }

    /// <summary>The text on one line, its whitespace runs collapsed to single spaces.</summary>
    internal static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A short reason for <c>memory_runs.message</c>: a status, never a response body or transcript text.</summary>
    internal static string Describe(Exception error) => error switch
    {
        LlmException { StatusCode: { } code } => $"HTTP {code}",
        LlmException llm => llm.Message,
        OperationCanceledException or TimeoutException => "timeout",
        HttpRequestException => "connection refused",
        _ => "unexpected error",
    };
}
