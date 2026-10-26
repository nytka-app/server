using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Ai;

/// <summary>
/// <c>classify-conversations</c> (Ai lane), queued by the scheduler: gives a closed conversation that has text and no sensitivity one,
/// from the start of its text alone, <see cref="BatchSize"/> at a time. Enrichment classifies what it summarizes, but a conversation
/// under <see cref="EnrichConversationHandler.MinWords"/> words is skipped and would stay unclassified, which readers treat as visible.
/// A conversation the model leaves out stays unclassified and is tried again on a later tick; the run repeats only while a batch made progress.
/// </summary>
public sealed class ClassifyConversationsHandler(ILlmClient llm, ConversationStore conversations, TimeProvider time) : IJobHandler
{
    public const int BatchSize = 30;

    /// <summary>How long a conversation must have been closed, so a late batch has had its chance to change it.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    /// <summary>How far ahead the scheduler queues a run, which also spaces the tries when the model keeps leaving one out.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);

    public const string SchemaName = "conversation_sensitivity";

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
        You get the start of the transcripts of short recorded conversations, one per line as "number: text". Give each a sensitivity: "health" (illness, symptoms, a doctor or a medicine), "therapy" (a therapy or counselling session, or what was said in one), "intimate" (sex or the private matters of a romantic relationship), "money" (debts, salary, account details or a private cost), or "none". Choose the one that fits best; most are "none". Judge the content of the words, whoever speaks them.
        Answer with one entry for every number, using the number you were given.
        """;

    public sealed record Entry(int N, string? Sensitivity);

    public sealed record Answer(IReadOnlyList<Entry> Classes);

    public string Kind => JobKinds.ClassifyConversations;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (!llm.IsConfigured)
        {
            return JobOutcome.Done;
        }

        var batch = await conversations.UnclassifiedAsync(BatchSize, time.GetUtcNow() - Quiet, ct);
        if (batch.Count == 0)
        {
            return JobOutcome.Done;
        }

        var user = string.Join('\n', batch.Select((c, i) => $"{i + 1}: {c.Text}"));
        var answer = LlmJson.Parse<Answer>(await llm.CompleteJsonAsync(new LlmRequest(SchemaName, Schema, System, user), ct));
        var set = await conversations.ClassifyAsync(Classes(answer, batch), ct);
        return set > 0 && batch.Count == BatchSize ? JobOutcome.RunAgain(TimeSpan.FromSeconds(1)) : JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The model's entries cut to numbers it was given and classes it may use; the first entry for a number wins.</summary>
    public static IReadOnlyList<(Guid Id, string Sensitivity)> Classes(Answer answer, IReadOnlyList<ConversationStore.UnclassifiedConversation> batch)
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
