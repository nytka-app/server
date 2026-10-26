using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Server.Speech;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Labeling;

/// <summary>
/// The <c>pick-labeling</c> job (docs/explanation/features.md, Labelling): tops each task's open items up to <see cref="Pool"/>, each
/// reason to its share, from a random sample of the conversations that still hold audio. It reads stored scores and decodes only the
/// picked items' audio, for their peaks. It runs in the Audio lane, once an hour.
/// </summary>
public sealed class PickLabelingHandler(
    LabelStore labels, BatchStore batches, SettingsService settings, TimeProvider time, ILogger<PickLabelingHandler> logger) : IJobHandler
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    /// <summary>The open items a task keeps: a few sessions' worth.</summary>
    public const int Pool = 150;

    public const int SampleConversations = 200;

    public string Kind => JobKinds.PickLabeling;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var counts = (await labels.OpenCountsAsync(ct)).ToLookup(c => c.Task);
        var sample = (await labels.SampleConversationsAsync(SampleConversations, ct)).ToArray();
        var lines = sample.Length > 0 ? await labels.CandidateLinesAsync(sample, ct) : [];
        var drafts = sample.Length > 0 ? await labels.DraftsAsync(sample, ct) : [];

        // quiet waits for a per-line level, which no stored row holds yet: it has no candidates.
        var units = new Dictionary<string, IReadOnlyList<LabelingUnit>>
        {
            [LabelingTasks.Device] = [.. LabelingCandidates.Device(lines, SpeechSettings.MediaThreshold(settings))],
            [LabelingTasks.Voice] = [.. LabelingCandidates.Voice(lines, VoiceSettings.UserThreshold(settings))],
            [LabelingTasks.Transcript] = [.. LabelingCandidates.Transcript(lines, drafts)],
            [LabelingTasks.Overlap] = [.. LabelingCandidates.Overlap(lines)],
        };

        var added = 0;
        foreach (var (task, taskUnits) in units)
        {
            var open = counts[task].ToDictionary(c => c.Reason, c => c.Count);
            var picked = LabelingCandidates.Pick(taskUnits, open, Pool, Random.Shared);
            var repeats = (int)Math.Round(Pool * LabelingCandidates.Shares["repeat"]) - open.GetValueOrDefault("repeat");
            if (repeats > 0)
            {
                picked.AddRange(await labels.RepeatCandidatesAsync(task, repeats, now, ct));
            }

            var withPeaks = new List<(LabelingCandidate, float[]?)>();
            foreach (var c in picked)
            {
                var bodies = await batches.SpeechAudioBodiesAsync(
                    c.ConversationId, new DateTimeOffset(c.StartedAt, TimeSpan.Zero), new DateTimeOffset(c.EndedAt, TimeSpan.Zero), ct);
                withPeaks.Add((c, LabelingPeaks.Measure(bodies, c.StartedAt, c.EndedAt)));
            }

            added += await labels.AddItemsAsync(task, withPeaks, now, ct);
        }

        logger.LogInformation("Labelling added {Items} item(s) from {Conversations} conversation(s).", added, sample.Length);
        return JobOutcome.RunAgain(Every);
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
}
