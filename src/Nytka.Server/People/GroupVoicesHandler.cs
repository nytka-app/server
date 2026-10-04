using Nytka.Audio.Voice;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>
/// <c>group-voices</c> (Audio lane, so it never runs beside a <c>transcribe</c>; no network, no model call): takes the
/// fingerprints of segments that are not the wearer's and have no person, in segment order, and for each one
/// (docs/specs/people.md, Layer 2) adds it to the pending match of the person whose voiceprint it reaches
/// <c>people.voiceThreshold</c> with, else joins the group whose centroid it reaches it with, else starts a group. A label never changes
/// here: only a confirmation links segments to a person. The log carries counts only.
/// </summary>
public sealed partial class GroupVoicesHandler(
    VoiceGroupStore groups, SpeakerModel model, SettingsService settings, TimeProvider time, ILogger<GroupVoicesHandler> logger) : IJobHandler
{
    /// <summary>One run takes at most this many fingerprints; the scheduler queues the next run while some wait.</summary>
    public const int MaxPerRun = 1000;

    private const int PageSize = 200;

    public string Kind => JobKinds.GroupVoices;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (!PeopleSettings.VoiceMatching(settings) || model.Id is not { } modelId)
        {
            return JobOutcome.Done;
        }

        var threshold = PeopleSettings.VoiceThreshold(settings);
        var voiceprints = await groups.VoiceprintsAsync(modelId, ct);
        var clusters = (await groups.GroupsAsync(modelId, ct)).ToList();
        int matched = 0, joined = 0, started = 0, taken = 0;

        while (taken < MaxPerRun && await groups.UngroupedAsync(modelId, Math.Min(PageSize, MaxPerRun - taken), ct) is { Count: > 0 } page)
        {
            foreach (var fingerprint in page)
            {
                taken++;
                var now = time.GetUtcNow();
                if (Best(voiceprints, fingerprint.Vector) is { } person && person.Similarity >= threshold)
                {
                    await groups.AddToMatchAsync(fingerprint.SegmentId, fingerprint.ConversationId, person.Id, person.Similarity, now, ct);
                    matched++;
                    continue;
                }

                if (Best(clusters, fingerprint.Vector) is { } group && group.Similarity >= threshold)
                {
                    if (await groups.JoinGroupAsync(fingerprint.SegmentId, group.Id, fingerprint.Vector, now, ct) is { } centroid)
                    {
                        clusters[clusters.FindIndex(c => c.Id == group.Id)] = new VoiceCentroid(group.Id, centroid);
                        joined++;
                        continue;
                    }

                    // The group went (confirmed or emptied) since it was read.
                    clusters = (await groups.GroupsAsync(modelId, ct)).ToList();
                }

                if (await groups.StartGroupAsync(fingerprint.SegmentId, modelId, fingerprint.Vector, now, ct) is { } created)
                {
                    clusters.Add(created);
                    started++;
                }
            }
        }

        LogGrouped(logger, taken, matched, joined, started);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    private static (Guid Id, float Similarity)? Best(IReadOnlyList<VoiceCentroid> centroids, float[] vector)
    {
        (Guid, float)? best = null;
        foreach (var centroid in centroids)
        {
            var similarity = SpeakerEmbedder.Cosine(vector, centroid.Vector);
            if (best is null || similarity > best.Value.Item2)
            {
                best = (centroid.Id, similarity);
            }
        }

        return best;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice grouping took {Taken} fingerprint(s): {Matched} matched a voiceprint, {Joined} joined a group, {Started} started one.")]
    private static partial void LogGrouped(ILogger logger, int taken, int matched, int joined, int started);
}
