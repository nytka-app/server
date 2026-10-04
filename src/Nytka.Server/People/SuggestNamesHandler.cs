using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Memories;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>
/// The <c>suggest-names</c> job (docs/specs/people.md, Layer 1): asks the model for names of a conversation's unnamed voices
/// and stores the ones that pass the drop rules as pending suggestions. It changes no label. Retries as
/// <see cref="ExtractMemoriesHandler"/> does: the runner makes three attempts, and a failed run comes back an hour later,
/// three rounds at most.
/// </summary>
public sealed class SuggestNamesHandler(
    ILlmClient llm,
    IOptionsMonitor<LlmOptions> llmOptions,
    SettingsService settings,
    NameSuggestionStore suggestions,
    TimeProvider time,
    ILogger<SuggestNamesHandler> logger)
    : IJobHandler
{
    public const int MaxNameLength = 80;
    public const double MinConfidence = 0.5;

    public string Kind => JobKinds.SuggestNames;

    public sealed record Suggestion(string Voice, string Name, long SegmentId, double Confidence);

    public sealed record Answer(IReadOnlyList<Suggestion> Suggestions);

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var conversationId = JsonSerializer.Deserialize<SuggestNamesPayload>(job.Payload)!.ConversationId;
        if (!PeopleSettings.SuggestNames(settings) || !llm.IsConfigured)
        {
            await suggestions.ForgetRunAsync(conversationId, ct);
            return JobOutcome.Done;
        }

        if (await suggestions.ReadInputAsync(conversationId, ct) is not { } input)
        {
            return JobOutcome.Done;
        }

        try
        {
            var run = await suggestions.GetRunAsync(conversationId, ct);
            var covered = input.LastSegmentId is null || run?.ThroughSegmentId >= input.LastSegmentId;
            var brief = EnrichConversationHandler.IsBrief(input.Segments.Sum(s => EnrichConversationHandler.Words(s.Text)));
            var targets = NameTargets.Find(input.Segments);
            var candidates = covered || brief || targets.Count == 0 ? [] : await AskAsync(input, targets, ct);
            await suggestions.ApplyAsync(
                conversationId, candidates, covered ? run?.ThroughSegmentId : input.LastSegmentId, time.GetUtcNow(), ct);
            return JobOutcome.Done;
        }
        catch (Exception error) when (job.Attempts >= JobRunner.MaxAttempts && !ct.IsCancellationRequested)
        {
            // Only the third attempt records the failure; the earlier ones throw and the runner tries again.
            var message = ExtractMemoriesHandler.Describe(error);
            logger.LogWarning(error, "Suggesting names failed after {Attempts} attempts: {Message}", job.Attempts, message);
            var failures = await suggestions.MarkFailedAsync(conversationId, message, time.GetUtcNow(), ct);
            return failures is { } count && count < ExtractMemoriesHandler.MaxRounds
                ? JobOutcome.RunAgain(ExtractMemoriesHandler.RetryAfter)
                : JobOutcome.Done;
        }
    }

    /// <summary>The run's failure is recorded by <see cref="RunAsync"/> on its last attempt, so there is nothing left to do.</summary>
    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    private async Task<IReadOnlyList<NameCandidate>> AskAsync(NameInput input, IReadOnlyList<NameTarget> targets, CancellationToken ct)
    {
        var zone = UserTimeZone.Resolve(settings);
        var windows = TranscriptWindows.Split(NamePrompt.Lines(input, targets, zone), llmOptions.CurrentValue.MaxInputChars);
        var people = await suggestions.PeopleByNameAsync(ct);
        var userName = MemorySettings.UserName(settings);

        var pooled = new List<Suggestion>();
        foreach (var window in windows)
        {
            var answer = LlmJson.Parse<Answer>(await llm.CompleteJsonAsync(
                new LlmRequest(NamePrompt.SchemaName, NamePrompt.Schema, NamePrompt.System,
                    NamePrompt.UserMessage(input, userName, people.Values.Select(p => p.Name).Order(), window, zone)), ct));
            pooled.AddRange(answer.Suggestions);
        }

        return Apply(pooled, targets, input.Segments.Select(s => s.Id).ToHashSet(), userName, people);
    }

    /// <summary>
    /// The drop rules: a suggestion goes when its voice is not one sent, its segment is not in the conversation, its name is
    /// empty, over 80 characters or the wearer's, or its confidence is under 0.5. Of the rest, one per target, the most
    /// confident. A name equal to a person's carries that person.
    /// </summary>
    public static IReadOnlyList<NameCandidate> Apply(
        IEnumerable<Suggestion> pooled, IReadOnlyList<NameTarget> targets, IReadOnlySet<long> segmentIds, string? userName,
        IReadOnlyDictionary<string, PersonName> people)
    {
        var best = new Dictionary<char, NameCandidate>();
        foreach (var suggestion in pooled)
        {
            var name = ExtractMemoriesHandler.OneLine(suggestion.Name);
            if (VoiceOf(suggestion.Voice, targets) is not { } target
                || !segmentIds.Contains(suggestion.SegmentId)
                || name.Length == 0
                || name.EnumerateRunes().Count() > MaxNameLength
                || string.Equals(name, userName, StringComparison.OrdinalIgnoreCase)
                || !(suggestion.Confidence >= MinConfidence)
                || best.TryGetValue(target.Letter, out var known) && known.Confidence >= suggestion.Confidence)
            {
                continue;
            }

            var person = people.GetValueOrDefault(name.ToLowerInvariant());
            best[target.Letter] = new NameCandidate(
                target.Kind, target.SpeakerId, target.SegmentIds, person?.Name ?? name, person?.Id, suggestion.SegmentId,
                (float)Math.Min(suggestion.Confidence, 1));
        }

        return best.Values.ToList();
    }

    /// <summary>The target a voice names: "Voice A" as sent, or just the letter.</summary>
    private static NameTarget? VoiceOf(string voice, IReadOnlyList<NameTarget> targets)
    {
        var text = voice.Trim();
        if (text.StartsWith("Voice ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[6..].Trim();
        }

        return text.Length == 1 ? targets.FirstOrDefault(t => char.ToUpperInvariant(text[0]) == t.Letter) : null;
    }
}
