using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Memories;

/// <summary>
/// <c>classify-memories</c> (Ai lane), queued by <c>POST /api/v1/memories/classify</c>: gives every live memory that has no
/// sensitivity one, <see cref="BatchSize"/> at a time, from its text alone. Extraction classifies the memories it writes; this
/// covers the ones from before the class existed and the ones a person added. A memory the model leaves out stays unclassified
/// (and so visible) and is picked up by the next run; the run repeats while a batch made progress.
/// </summary>
public sealed class ClassifyMemoriesHandler(ILlmClient llm, MemoryStore memories) : IJobHandler
{
    public const int BatchSize = 100;

    public const string SchemaName = "memory_sensitivity";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["classes"],
          "properties": { "classes": { "type": "array", "items": {
            "type": "object", "additionalProperties": false, "required": ["n", "sensitivity"],
            "properties": { "n": { "type": "integer" },
                            "sensitivity": { "type": "string", "enum": ["none", "health", "therapy", "intimate", "money"] } } } } } }
        """;

    public const string System =
        """
        You get facts about one person, one per line as "number: fact". Give each fact a sensitivity: "health" (illness, symptoms, a doctor or a medicine), "therapy" (a therapy or counselling session, or what was said in one), "intimate" (sex or the private matters of a romantic relationship), "money" (debts, salary, account details or a private cost), or "none". Choose the one that fits best; most facts are "none".
        Answer with one entry for every number, using the number you were given.
        """;

    public sealed record Entry(int N, string? Sensitivity);

    public sealed record Answer(IReadOnlyList<Entry> Classes);

    public string Kind => JobKinds.ClassifyMemories;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (!llm.IsConfigured)
        {
            return JobOutcome.Done;
        }

        var batch = await memories.UnclassifiedAsync(BatchSize, ct);
        if (batch.Count == 0)
        {
            return JobOutcome.Done;
        }

        var user = string.Join('\n', batch.Select((m, i) => $"{i + 1}: {m.Text}"));
        var answer = LlmJson.Parse<Answer>(await llm.CompleteJsonAsync(new LlmRequest(SchemaName, Schema, System, user), ct));
        var set = await memories.ClassifyAsync(Classes(answer, batch), ct);
        return set > 0 && batch.Count == BatchSize ? JobOutcome.RunAgain(TimeSpan.FromSeconds(1)) : JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The model's entries cut to numbers it was given and classes it may use; the first entry for a number wins.</summary>
    public static IReadOnlyList<(Guid Id, string Sensitivity)> Classes(Answer answer, IReadOnlyList<KnownMemory> batch)
    {
        var seen = new HashSet<int>();
        var classes = new List<(Guid, string)>();
        foreach (var entry in answer.Classes)
        {
            if (entry.N >= 1 && entry.N <= batch.Count && Sensitivity.Parse(entry.Sensitivity) is { } value && seen.Add(entry.N))
            {
                classes.Add((batch[entry.N - 1].Id, value));
            }
        }

        return classes;
    }
}
