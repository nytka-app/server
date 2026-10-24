using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Quality;

/// <summary>
/// What the scheduler's scan queues for the quality metrics, from the first conversation on: today once <c>digest.hour</c> has come, yesterday again once it
/// has ended (its evening and late audio), and the earlier days since the first conversation that have no row, oldest first
/// and <see cref="BackfillPerTick"/> at a time. Dedupe keys make every call safe to repeat.
/// </summary>
public sealed class QualityQueue(JobQueue queue, QualityStore quality, SettingsService settings)
{
    public const int BackfillPerTick = 31;

    public async Task QueueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var zone = UserTimeZone.Resolve(settings);
        var today = DigestDay.Today(now, zone);
        var yesterday = today.AddDays(-1);
        if (await quality.FirstConversationAtAsync(ct) is not { } at)
        {
            return;
        }

        var first = DigestDay.Today(new DateTimeOffset(at, TimeSpan.Zero), zone);
        if (first > yesterday)
        {
            first = yesterday;
        }

        var measured = await quality.MeasuredSinceAsync(DigestDay.Text(first), ct);
        var due = Enumerable.Range(0, Math.Max(0, yesterday.DayNumber - first.DayNumber))
            .Select(first.AddDays)
            .Where(d => !measured.ContainsKey(DigestDay.Text(d)))
            .Take(BackfillPerTick)
            .ToList();

        if (!measured.TryGetValue(DigestDay.Text(yesterday), out var last) || last < DigestDay.Bounds(yesterday, zone).To)
        {
            due.Add(yesterday);
        }

        if (DigestDay.HourOf(now, zone) >= DigestSettings.Hour(settings) && !measured.ContainsKey(DigestDay.Text(today)))
        {
            due.Add(today);
        }

        foreach (var text in due.Select(DigestDay.Text))
        {
            await queue.EnqueueAsync(JobKinds.MeasureQuality, new QualityPayload(text), JobKinds.MeasureQualityKey(text), now, ct);
        }
    }
}
