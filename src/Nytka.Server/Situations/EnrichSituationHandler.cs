using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Situations;

/// <summary>
/// <c>enrich-situation</c> (Ai lane): gives a situation a title and a summary from its own lines (docs/explanation/features.md#situations).
/// A situation with fewer than <see cref="EnrichConversationHandler.MinWords"/> spoken words is recorded as skipped and costs no call. The
/// job runner supplies the attempts; after the last, <see cref="OnGiveUpAsync"/> records <c>failed</c> and the scan brings it back an
/// hour later, up to <see cref="EnrichConversationHandler.MaxFailedRounds"/> rounds. Tasks, facts and tags stay with the conversation's own
/// run. Nothing of the transcript or the answer reaches the log.
/// </summary>
public sealed partial class EnrichSituationHandler(
    SituationStore situations, ConversationStore conversations, ILlmClient llm, IOptionsMonitor<LlmOptions> options, SettingsService settings,
    TimeProvider time, ILogger<EnrichSituationHandler> logger)
    : IJobHandler
{
    private static readonly TimeSpan RateLimitWait = TimeSpan.FromMinutes(1);

    public string Kind => JobKinds.EnrichSituation;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var id = JsonSerializer.Deserialize<EnrichSituationPayload>(job.Payload)!.SituationId;
        if (!SituationSettings.Enabled(settings) || !llm.IsConfigured || await situations.LinesOfAsync(id, ct) is not { } found)
        {
            return JobOutcome.Done;
        }

        var held = found.Segments.ToHashSet();
        var segments = (await conversations.SegmentsAsync(found.ConversationId, ct)).Where(s => held.Contains(s.Id)).ToList();
        if (EnrichConversationHandler.SpokenWords(segments) < EnrichConversationHandler.MinWords)
        {
            await situations.StoreEnrichmentAsync(
                id, found.LinesHash, SituationStore.AiSkipped, null, null, EnrichConversationHandler.TooShort, time.GetUtcNow(), ct);
            return JobOutcome.Done;
        }

        var zone = UserTimeZone.Resolve(settings);
        var windows = TranscriptWindows.Split(
            TranscriptText.Render(EnrichConversationHandler.WithoutMediaText(segments), zone), options.CurrentValue.MaxInputChars);
        if (windows.Count > EnrichConversationHandler.MaxWindows)
        {
            throw new LlmException("The situation is too long to summarize.");
        }

        var language = options.CurrentValue.OutputLanguage;
        var zoneName = UserTimeZone.Name(zone);
        var startedAt = new DateTimeOffset(DateTime.SpecifyKind(segments[0].StartedAt, DateTimeKind.Utc));
        SituationAnswer answer;
        try
        {
            var parts = new List<SituationAnswer>();
            for (var i = 0; i < windows.Count; i++)
            {
                parts.Add(await AskAsync(
                    SituationPrompt.System(language, zoneName), ConversationPrompt.User(startedAt, windows[i], i + 1, windows.Count, zone), ct));
            }

            answer = parts.Count == 1
                ? parts[0]
                : await AskAsync(
                    SituationPrompt.SystemForMerge(language, zoneName),
                    $"{ConversationPrompt.DateLine(startedAt, zone)}\n\n" + string.Join('\n', parts.Select((p, i) =>
                        $"Part {i + 1} of {parts.Count}: " + JsonSerializer.Serialize(p, JsonSerializerOptions.Web))),
                    ct);
        }
        catch (LlmException error) when (error.RateLimited)
        {
            return JobOutcome.RunAgain(RateLimitWait);
        }

        var title = ConversationPrompt.Cut(answer.Title, SituationPrompt.MaxTitle);
        var summary = ConversationPrompt.Cut(answer.Summary, SituationPrompt.MaxSummary);
        var stored = await situations.StoreEnrichmentAsync(
            id, found.LinesHash, SituationStore.AiDone, title.Length == 0 ? null : title, summary.Length == 0 ? null : summary, null,
            time.GetUtcNow(), ct);
        LogEnriched(logger, segments.Count, stored);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) =>
        situations.FailEnrichmentAsync(
            JsonSerializer.Deserialize<EnrichSituationPayload>(job.Payload)!.SituationId,
            error is LlmException ? error.Message : "The summary could not be made.", time.GetUtcNow(), ct);

    private async Task<SituationAnswer> AskAsync(string system, string user, CancellationToken ct) =>
        LlmJson.Parse<SituationAnswer>(await llm.CompleteJsonAsync(new LlmRequest(SituationPrompt.SchemaName, SituationPrompt.Schema, system, user), ct));

    [LoggerMessage(Level = LogLevel.Information, Message = "Situation summarized from {Segments} segment(s); stored: {Stored}.")]
    private static partial void LogEnriched(ILogger logger, int segments, bool stored);
}
