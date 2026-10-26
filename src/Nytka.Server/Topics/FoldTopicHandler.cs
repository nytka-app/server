using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Topics;

/// <summary>The payload of <c>fold-topic</c>.</summary>
public sealed record FoldTopicPayload(Guid TopicId);

/// <summary>
/// <c>fold-topic</c> (Ai lane): folds up to <see cref="Batch"/> of a topic's conversations not yet on its page into the running
/// summary, oldest first, with one model call, and runs again while more wait. Only summaries are read, never transcripts, so a
/// new conversation costs one call whatever the topic's size. A topic whose call fails <see cref="JobRunner.MaxAttempts"/> times
/// counts a failure, and after <see cref="MaxFailures"/> the scheduler leaves it until a fold succeeds by hand.
/// </summary>
public sealed class FoldTopicHandler(
    ILlmClient llm, IOptionsMonitor<LlmOptions> llmOptions, SettingsService settings, TopicStore topics, TimeProvider time) : IJobHandler
{
    public const int Batch = 10;

    public const int MaxFailures = 3;

    /// <summary>How long a fold waits after the model endpoint said 429. That is no failure: the page keeps its tries.</summary>
    public static readonly TimeSpan RateLimitWait = TimeSpan.FromMinutes(10);

    public string Kind => JobKinds.FoldTopic;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<FoldTopicPayload>(job.Payload)!;
        if (!llm.IsConfigured || !TopicSettings.Enabled(settings) || await topics.GetAsync(payload.TopicId, ct) is not { } topic)
        {
            return JobOutcome.Done;
        }

        var pending = await topics.PendingAsync(topic.Id, Batch, ct);
        if (pending.Count == 0)
        {
            return JobOutcome.Done;
        }

        var zone = UserTimeZone.Resolve(settings);
        string raw;
        try
        {
            raw = await llm.CompleteJsonAsync(
                new LlmRequest(
                    TopicPrompt.SchemaName, TopicPrompt.Schema,
                    TopicPrompt.System(llmOptions.CurrentValue.OutputLanguage, topic.Kind, UserTimeZone.Name(zone)),
                    TopicPrompt.User(topic.Name, topic.Summary, pending, zone)),
                ct);
        }
        catch (LlmException error) when (error.RateLimited)
        {
            return JobOutcome.RunAgain(RateLimitWait);
        }

        var answer = LlmJson.Parse<TopicAnswer>(raw);
        var summary = ConversationPrompt.Cut(answer.Summary, TopicPrompt.MaxSummary);
        if (summary.Length == 0)
        {
            throw new LlmException("The language model endpoint answered with an empty topic page.");
        }

        await topics.FoldAsync(topic.Id, summary, [.. pending.Select(p => p.ConversationId)], time.GetUtcNow(), ct);
        return pending.Count == Batch ? JobOutcome.RunAgain(TimeSpan.Zero) : JobOutcome.Done;
    }

    public async Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) =>
        await topics.FailedAsync(JsonSerializer.Deserialize<FoldTopicPayload>(job.Payload)!.TopicId, ct);
}
