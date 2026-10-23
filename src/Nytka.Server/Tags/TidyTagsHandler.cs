using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Tags;

/// <summary>The payload of <c>tidy-tags</c>: the owner's local date it runs for.</summary>
public sealed record TidyTagsPayload(string LocalDate);

/// <summary>
/// <c>tidy-tags</c> (Ai lane), once a local day while <c>tags.auto</c> is on (docs/specs/tags.md, Automatic tags): applies the
/// conversation proposals still pending from before tags applied themselves, retires the tags
/// enrichment applied that stayed on one conversation for <see cref="RetireAfter"/>, then asks the model which tags in use mean the
/// same and merges each group into its most used tag, keeping the others as aliases. Without a model only the retiring runs. A run
/// that fails its attempts is recorded as failed, so a broken model costs one try a day.
/// </summary>
public sealed partial class TidyTagsHandler(
    ILlmClient llm, SettingsService settings, TagStore tags, TimeProvider time, ILogger<TidyTagsHandler> logger) : IJobHandler
{
    public static readonly TimeSpan RetireAfter = TimeSpan.FromDays(60);

    /// <summary>The most tags sent to the model; a vocabulary this large is already a problem the owner can see.</summary>
    public const int MaxVocabulary = 300;

    public const string SchemaName = "tag_synonyms";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["groups"],
          "properties": { "groups": { "type": "array", "items": { "type": "array", "items": { "type": "string" } } } } }
        """;

    public const string System =
        """
        You get the tags that label a person's recorded conversations, one per line with how many items use it. Group the tags that name the same topic or setting: synonyms, the same word in Ukrainian, Russian and English, singular and plural, or another spelling. Never group tags that are only related ("work" and "meeting" are different, so are "doctor" and "health").
        Return only groups of two or more tags, each tag in at most one group, every tag copied exactly as listed. Return an empty list when no tags mean the same.
        """;

    public sealed record Answer(IReadOnlyList<IReadOnlyList<string>> Groups);

    public string Kind => JobKinds.TidyTags;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<TidyTagsPayload>(job.Payload)!;
        if (!TagSettings.Auto(settings) || await tags.TidiedAsync(payload.LocalDate, ct))
        {
            return JobOutcome.Done;
        }

        var now = time.GetUtcNow();
        await tags.ApplyPendingAsync(now, ct);
        var retired = await tags.RetireAsync(now - RetireAfter, ct);
        var merged = 0;
        var vocabulary = await tags.VocabularyAsync(MaxVocabulary, ct);
        if (llm.IsConfigured && vocabulary.Count > 1)
        {
            var answer = LlmJson.Parse<Answer>(await llm.CompleteJsonAsync(
                new LlmRequest(SchemaName, Schema, System, string.Join('\n', vocabulary.Select(t => $"{t.Name} ({t.Uses})"))), ct));
            foreach (var group in Groups(answer, vocabulary))
            {
                if (await tags.MergeAsync(group[0], group.Skip(1).ToList(), now, ct))
                {
                    merged += group.Count - 1;
                }
            }
        }

        await tags.RecordTidyAsync(payload.LocalDate, merged, retired, failed: false, now, ct);
        LogTidied(logger, merged, retired);
        return JobOutcome.Done;
    }

    public async Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<TidyTagsPayload>(job.Payload)!;
        await tags.RecordTidyAsync(payload.LocalDate, 0, 0, failed: true, time.GetUtcNow(), ct);
    }

    /// <summary>
    /// The model's groups cut to tags in use, each tag in one group at most, groups of two or more; each starts with the tag to keep: the
    /// most used, then the oldest. The model is not trusted with that choice.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Groups(Answer answer, IReadOnlyList<TagUse> vocabulary)
    {
        var known = vocabulary.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<IReadOnlyList<string>>();
        foreach (var group in answer.Groups)
        {
            var members = group.Select(TagName.Normalize).OfType<string>().Where(known.ContainsKey).Distinct().Where(taken.Add).ToList();
            if (members.Count > 1)
            {
                groups.Add([.. members.OrderByDescending(n => known[n].Uses).ThenBy(n => known[n].CreatedAt).ThenBy(n => n, StringComparer.Ordinal)]);
            }
        }

        return groups;
    }

    /// <summary>The local hour from which the scheduler queues the day's run.</summary>
    public const int Hour = 4;

    [LoggerMessage(LogLevel.Information, "Tags tidied: {Merged} merged into synonyms, {Retired} one-off links retired.")]
    private static partial void LogTidied(ILogger logger, int merged, int retired);
}
