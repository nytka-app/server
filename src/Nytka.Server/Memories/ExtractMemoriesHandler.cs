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
            "additionalProperties": false, "required": ["text", "replaces"],
            "properties": { "text": { "type": "string" }, "replaces": { "type": ["string", "null"] } } } } } }
        """;

    public string Kind => JobKinds.ExtractMemories;

    public sealed record Candidate(string Text, string? Replaces);

    public sealed record Answer(IReadOnlyList<Candidate> Memories);

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var conversationId = JsonSerializer.Deserialize<ExtractPayload>(job.Payload)!.ConversationId;
        if (!MemorySettings.IsEnabled(settings) || !llm.IsConfigured)
        {
            return JobOutcome.Done;
        }

        if (await memories.ReadInputAsync(conversationId, ct) is not { } input)
        {
            return JobOutcome.Done;
        }

        try
        {
            var run = await memories.GetRunAsync(conversationId, ct);
            var covered = input.LastSegmentId is null || run?.ThroughSegmentId >= input.LastSegmentId;
            var candidates = covered ? [] : await AskAsync(input, ct);
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
        var lines = TranscriptText.Render(input.Segments.Select(s =>
            new TranscriptSegment(new DateTimeOffset(s.StartedAt, TimeSpan.Zero), s.Speaker, s.Text)));
        var windows = TranscriptWindows.Split(lines, llmOptions.CurrentValue.MaxInputChars);
        var known = await memories.NewestAsync(KnownMemories, ct);
        var listed = known.Select(k => k.Id).ToHashSet();
        var system = SystemMessage(llmOptions.CurrentValue.OutputLanguage);
        var userName = MemorySettings.UserName(settings);

        var pooled = new List<MemoryCandidate>();
        foreach (var window in windows)
        {
            var answer = LlmJson.Parse<Answer>(await llm.CompleteJsonAsync(
                new LlmRequest(SchemaName, Schema, system, UserMessage(input, userName, known, window)), ct));
            foreach (var candidate in answer.Memories)
            {
                var text = Cut(candidate.Text);
                var fingerprint = TextFingerprint.Of(text);
                if (fingerprint.Length == 0)
                {
                    continue;
                }

                Guid? replaces = Guid.TryParse(candidate.Replaces, out var id) && listed.Contains(id) ? id : null;
                pooled.Add(new MemoryCandidate(text, fingerprint, replaces));
            }
        }

        return pooled.Take(MaxMemoriesPerRun).ToList();
    }

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

    public static string SystemMessage(string outputLanguage) =>
        Prompt(string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase) ? "the language of the conversation" : outputLanguage);

    private static string Prompt(string outputLanguage) =>
        $"""
        You read a transcript of a conversation and pick out lasting facts about one person, called "you" below.
        A lasting fact is true beyond this conversation: who you are, your family, friends, home, work, health,
        habits, preferences, goals and commitments.
        Never report one-off events, tasks, plans for a single day or other people's affairs.
        Speaker labels may differ between parts of the transcript; the user message says who "you" is.
        Write each fact as one short sentence, at most {MaxTextLength} characters, in {outputLanguage}.
        Return at most {MaxMemoriesPerRun} facts, none that a known memory already states. When a fact updates a
        known memory, set "replaces" to that memory's id, otherwise to null. Return an empty list when there is
        nothing lasting.
        """;

    public static string UserMessage(ExtractionInput input, string? userName, IReadOnlyList<KnownMemory> known, string transcript)
    {
        var message = new StringBuilder();
        message.Append("Conversation: ").Append(string.IsNullOrWhiteSpace(input.Title) ? "(untitled)" : input.Title).Append('\n');
        message.Append("Date: ").Append(input.StartedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
        message.Append("\"You\" is ").Append(userName ?? "the person wearing the pendant").Append(".\n\n");
        message.Append("Known memories (id: text):\n");
        message.Append(known.Count == 0 ? "(none)" : string.Join('\n', known.Select(k => $"{k.Id}: {k.Text}")));
        message.Append("\n\nTranscript:\n").Append(transcript);
        return message.ToString();
    }

    /// <summary>One line, at most <see cref="MaxTextLength"/> characters (Postgres counts code points, as runes do).</summary>
    public static string Cut(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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

    /// <summary>A short reason for <c>memory_runs.message</c>: a status, never a response body or transcript text.</summary>
    private static string Describe(Exception error) => error switch
    {
        LlmException { StatusCode: { } code } => $"HTTP {code}",
        LlmException llm => llm.Message,
        OperationCanceledException or TimeoutException => "timeout",
        HttpRequestException => "connection refused",
        _ => "unexpected error",
    };
}
