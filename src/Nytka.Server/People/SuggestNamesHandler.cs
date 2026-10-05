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
    public const double MinConfidence = 0.5;

    public string Kind => JobKinds.SuggestNames;

    public sealed record Suggestion(string Voice, string? Name, long SegmentId, double Confidence, string? Role);

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
            var covered = input.LastSegmentId is null
                || run is { Validator: >= NameValidator.Version } && run.ThroughSegmentId >= input.LastSegmentId;
            var brief = EnrichConversationHandler.IsBrief(input.Segments.Sum(s => EnrichConversationHandler.Words(s.Text)));
            var targets = NameTargets.Find(input.Segments);
            var candidates = covered || brief || targets.Count == 0 ? [] : await AskAsync(input, targets, ct);
            await suggestions.ApplyAsync(
                conversationId, candidates, covered ? run?.ThroughSegmentId : input.LastSegmentId, NameValidator.Version, time.GetUtcNow(),
                ct);
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

        return Apply(pooled, targets, input.Segments, userName, people);
    }

    /// <summary>
    /// The drop rules: a suggestion goes when its voice is not one sent or is the wearer's, or its confidence is under 0.5. Its
    /// name is kept when it passes <see cref="NameValidator.IsName"/>, is not the wearer's (<paramref name="userName"/> or a
    /// name the wearer gave for themselves) and occurs in a segment of two words or more near the one named (<see cref="NameValidator.Evidence"/>).
    /// Its role (docs/specs/tags.md, Roles) is kept when, as a tag name, it passes <see cref="NameValidator.IsRole"/>, is not
    /// one the wearer gave for themselves and occurs in a segment near the one named; a person known only by role gets no role.
    /// With neither the suggestion goes; with a role only it is a role-only candidate (the role's display form as the name,
    /// <c>Named</c> false). Of the rest, one per target, the most confident, with the segment that says the name (else the
    /// role) as evidence. A name equal to a named person's carries that person; for a person known only by role the candidate
    /// carries that person, and a name equal to the role's own is dropped.
    /// </summary>
    public static IReadOnlyList<NameCandidate> Apply(
        IEnumerable<Suggestion> pooled, IReadOnlyList<NameTarget> targets, IReadOnlyList<NameSegment> segments, string? userName,
        IReadOnlyDictionary<string, PersonName> people)
    {
        var wearer = NameValidator.WearerNames(segments);
        if (userName is not null)
        {
            wearer = [.. wearer, userName];
        }

        var best = new Dictionary<char, NameCandidate>();
        foreach (var suggestion in pooled)
        {
            if (VoiceOf(suggestion.Voice, targets) is not { } target
                || target.SpeakerId is { } speaker && segments.Any(s => s.IsWearer && s.SpeakerId == speaker)
                || !(suggestion.Confidence >= MinConfidence)
                || best.TryGetValue(target.Letter, out var known) && known.Confidence >= suggestion.Confidence)
            {
                continue;
            }

            var name = suggestion.Name is null ? "" : ExtractMemoriesHandler.OneLine(suggestion.Name);
            var nameLine = name.Length > 0
                && !wearer.Any(w => NameValidator.SameName(name, w))
                && !(target.KnownAs is { } own && string.Equals(name, own, StringComparison.OrdinalIgnoreCase))
                && NameValidator.Evidence(name, suggestion.SegmentId, segments) is { } evidence
                && NameValidator.IsName(name, evidence.Text)
                    ? evidence
                    : null;
            var tag = target.Kind == "person" || suggestion.Role is null
                ? null
                : TagName.Normalize(ExtractMemoriesHandler.OneLine(suggestion.Role));
            var roleLine = tag is not null
                && NameValidator.IsRole(tag)
                && !NameValidator.IsWearerRole(tag, segments)
                    ? NameValidator.Evidence(NameValidator.RoleWords(tag), suggestion.SegmentId, segments, minWords: 1)
                    : null;
            if (nameLine is null && roleLine is null)
            {
                continue;
            }

            var confidence = (float)Math.Min(suggestion.Confidence, 1);
            var role = roleLine is null ? null : tag;
            if (nameLine is null)
            {
                // Never matched to a person by name: two repairmen are two people.
                best[target.Letter] = new NameCandidate(
                    target.Kind, target.SpeakerId, target.SegmentIds, TagName.Display(tag!), null, roleLine!.Id, confidence, role, Named: false);
                continue;
            }

            var person = target.Kind == "person" ? null : people.GetValueOrDefault(name.ToLowerInvariant());
            best[target.Letter] = new NameCandidate(
                target.Kind, target.SpeakerId, target.SegmentIds, person?.Name ?? name, target.PersonId ?? person?.Id, nameLine.Id, confidence, role);
        }

        return best.Values.ToList();
    }

    /// <summary>The target a voice names: "Voice A" as sent, or just the letter.</summary>
    private static NameTarget? VoiceOf(string voice, IReadOnlyList<NameTarget> targets)
    {
        var text = voice.Trim();
        if (text.IndexOf('(') is var bracket and >= 0)
        {
            text = text[..bracket].Trim();
        }

        if (text.StartsWith("Voice ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[6..].Trim();
        }

        return text.Length == 1 ? targets.FirstOrDefault(t => char.ToUpperInvariant(text[0]) == t.Letter) : null;
    }
}
