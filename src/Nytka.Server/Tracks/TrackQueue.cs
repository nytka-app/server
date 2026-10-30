using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Tracks;

/// <summary>
/// What the scheduler's scan does for speaker tracks: it records the effective <c>tracks.enabled</c> where the readers' SQL can see it,
/// and, while it is on, queues <c>build-tracks</c> for every local day that has ended and has a line with a fingerprint but no track
/// (a day nobody built, or lines that came late), oldest first and <see cref="PerTick"/> at a time. Dedupe keys make every call safe to repeat.
/// </summary>
public sealed class TrackQueue(JobQueue queue, TrackStore tracks, SpeakerModel model, SettingsService settings)
{
    /// <summary>
    /// The most days one tick queues: a build reads a day's fingerprints and clusters them in memory, so a backlog drains over a few
    /// ticks, and a week fits in one. A starting value.
    /// </summary>
    public const int PerTick = 7;

    public async Task QueueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var enabled = TrackSettings.Enabled(settings);
        await tracks.ApplyEnabledAsync(enabled, now, ct);
        if (!enabled || model.Id is not { } modelId)
        {
            return;
        }

        var zone = UserTimeZone.Resolve(settings);
        var before = DigestDay.Bounds(DigestDay.Today(now, zone), zone).From;
        foreach (var day in await tracks.DaysWithoutTracksAsync(modelId, UserTimeZone.Name(zone), before, PerTick, ct))
        {
            var text = DigestDay.Text(day);
            await queue.EnqueueAsync(JobKinds.BuildTracks, new TracksPayload(text), JobKinds.BuildTracksKey(text), now, ct);
        }
    }
}
