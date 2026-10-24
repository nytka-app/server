using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Rollups;

/// <summary>The payload of <c>make-rollup</c>: the period, its start (none for all time) and, for a topic's week, the topic.</summary>
public sealed record RollupPayload(string Period, string? Start, Guid? TopicId = null);

/// <summary>
/// <c>make-rollup</c> (Ai lane): one model call over a period's digests, summaries or shorter rollups, with the period before
/// for what changed. A month waits for its weeks, a year for its months and all time for both, queuing what is missing and
/// running again once it is there. A period with too little to say is stored locked without a call.
/// </summary>
public sealed class MakeRollupHandler(
    ILlmClient llm, IOptionsMonitor<LlmOptions> llmOptions, SettingsService settings, RollupStore rollups, TopicStore topics,
    JobQueue queue, TimeProvider time) : IJobHandler
{
    /// <summary>How long a rollup waits for the shorter ones it is made from.</summary>
    public static readonly TimeSpan WaitForParts = TimeSpan.FromMinutes(10);

    public string Kind => JobKinds.MakeRollup;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<RollupPayload>(job.Payload)!;
        if (!llm.IsConfigured || !RollupSettings.Enabled(settings) || !RollupPeriod.Periods.Contains(payload.Period))
        {
            return JobOutcome.Done;
        }

        var zone = UserTimeZone.Resolve(settings);
        var zoneName = zone.Id;
        var now = time.GetUtcNow();
        var today = DigestDay.Today(now, zone);
        string? topicName = null;
        DateOnly start;
        DateOnly end;
        RollupInput input;
        if (payload.Period == RollupStore.All)
        {
            if (await rollups.FirstDateAsync(zoneName, ct) is not { } first || !DigestDay.TryParse(first, out start))
            {
                return JobOutcome.Done;
            }

            end = RollupPeriod.StartOf(RollupStore.Month, today);
            var thisYear = RollupPeriod.StartOf(RollupStore.Year, today);
            var parts = await PartsAsync(RollupStore.Year, start, thisYear, today, ct);
            var months = await PartsAsync(RollupStore.Month, thisYear > start ? thisYear : start, end, today, ct);
            if (parts is null || months is null)
            {
                return JobOutcome.RunAgain(WaitForParts);
            }

            input = Combine([.. parts, .. months]);
        }
        else if (!DigestDay.TryParse(payload.Start, out start) || RollupPeriod.StartOf(payload.Period, start) != start
                 || (end = RollupPeriod.EndOf(payload.Period, start)) > today)
        {
            return JobOutcome.Done;
        }
        else if (payload.Period == RollupStore.Week)
        {
            var (from, _) = DigestDay.Bounds(start, zone);
            var (to, _) = DigestDay.Bounds(end, zone);
            if (payload.TopicId is { } topicId)
            {
                if (await topics.GetAsync(topicId, ct) is not { } topic)
                {
                    return JobOutcome.Done;
                }

                topicName = topic.Name;
                input = await rollups.TopicWeekAsync(topicId, from, to, zoneName, ct);
            }
            else
            {
                input = await rollups.WeekAsync(RollupPeriod.Text(start), RollupPeriod.Text(end), from, to, zoneName, ct);
            }
        }
        else
        {
            var child = payload.Period == RollupStore.Month ? RollupStore.Week : RollupStore.Month;
            if (await PartsAsync(child, start, end, today, ct) is not { } parts)
            {
                return JobOutcome.RunAgain(WaitForParts);
            }

            input = Combine(parts);
        }

        var need = payload.TopicId is null ? RollupPeriod.Need(payload.Period) : RollupPeriod.TopicNeed;
        var startText = RollupPeriod.Text(start);
        var endText = RollupPeriod.Text(end);
        if (input.Have < need || input.Lines.Count == 0)
        {
            await rollups.SaveAsync(payload.Period, startText, endText, payload.TopicId, input.Have, need, null, [], input.Digests, input.Conversations, now, ct);
            return JobOutcome.Done;
        }

        var previous = payload.Period == RollupStore.All
            ? await rollups.GetAsync(RollupStore.All, startText, null, ct)
            : await rollups.GetAsync(payload.Period, RollupPeriod.Text(RollupPeriod.Previous(payload.Period, start)), payload.TopicId, ct);
        var answer = LlmJson.Parse<RollupAnswer>(await llm.CompleteJsonAsync(
            new LlmRequest(
                RollupPrompt.SchemaName, RollupPrompt.Schema,
                RollupPrompt.System(llmOptions.CurrentValue.OutputLanguage, payload.Period, topicName),
                RollupPrompt.User(payload.Period, startText, endText, input, previous?.Summary)),
            ct));
        var summary = ConversationPrompt.Cut(answer.Summary, RollupPrompt.MaxSummary);
        if (summary.Length == 0)
        {
            throw new LlmException("The language model endpoint answered with an empty rollup.");
        }

        var changes = previous?.Summary is null
            ? []
            : answer.Changes.Select(c => ConversationPrompt.Cut(c, RollupPrompt.MaxChange)).Where(c => c.Length > 0).Take(RollupPrompt.MaxChanges).ToList();
        await rollups.SaveAsync(payload.Period, startText, endText, payload.TopicId, input.Have, need, summary, changes, input.Digests, input.Conversations, now, ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// The rollups of <paramref name="child"/> that make up [<paramref name="from"/>, <paramref name="to"/>), or null after queuing
    /// the closed ones that are missing.
    /// </summary>
    private async Task<IReadOnlyList<RollupRow>?> PartsAsync(string child, DateOnly from, DateOnly to, DateOnly today, CancellationToken ct)
    {
        var have = await rollups.WithinAsync(child, RollupPeriod.Text(from), RollupPeriod.Text(to), ct);
        var missing = RollupPeriod.Closed(child, from, today)
            .Where(s => s >= from && s < to)
            .Select(RollupPeriod.Text)
            .Except(have.Select(r => r.Start))
            .ToList();
        foreach (var start in missing)
        {
            await QueueAsync(queue, child, start, null, time.GetUtcNow(), ct);
        }

        return missing.Count == 0 ? have : null;
    }

    private static RollupInput Combine(IReadOnlyList<RollupRow> parts) => new(
        [.. parts.Where(p => p.Summary is not null).Select(p => new RollupLine(p.Start, $"{p.Period} from {p.Start}", p.Summary!))],
        parts.Sum(p => p.Digests), parts.Sum(p => p.Conversations), parts.Sum(p => p.Have));

    /// <summary>Queues one rollup; its dedupe key keeps one job per period, start and topic.</summary>
    public static Task QueueAsync(JobQueue queue, string period, string? start, Guid? topicId, DateTimeOffset now, CancellationToken ct) =>
        queue.EnqueueAsync(JobKinds.MakeRollup, new RollupPayload(period, start, topicId), JobKinds.MakeRollupKey(period, start, topicId), now, ct);
}
