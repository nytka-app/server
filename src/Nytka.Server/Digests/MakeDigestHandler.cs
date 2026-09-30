using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Memories;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Digests;

/// <summary>
/// The <c>make-digest</c> job (docs/specs/v0.7.md, Daily digest): one model call over a local day's conversations, tasks
/// and memories, stored with its <c>digest.ready</c> event in one transaction. The job runner makes the three attempts; on
/// the third the failure is logged here and the job comes back an hour later while its date is still today.
/// </summary>
public sealed class MakeDigestHandler(
    ILlmClient llm,
    IOptionsMonitor<LlmOptions> llmOptions,
    SettingsService settings,
    DigestStore digests,
    NpgsqlDataSource dataSource,
    IEventPublisher events,
    TimeProvider time,
    ILogger<MakeDigestHandler> logger)
    : IJobHandler
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    public string Kind => JobKinds.MakeDigest;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<DigestPayload>(job.Payload)!;
        if (!DigestDay.TryParse(payload.LocalDate, out var date) || !llm.IsConfigured)
        {
            return JobOutcome.Done;
        }

        // A scheduled run follows the setting and keeps the first digest of a date; an on-demand one replaces it.
        if (!payload.Replace && (!DigestSettings.IsEnabled(settings) || await digests.ExistsAsync(payload.LocalDate, ct)))
        {
            return JobOutcome.Done;
        }

        var zone = UserTimeZone.Resolve(settings);
        var (from, to) = DigestDay.Bounds(date, zone);
        try
        {
            var input = await digests.ReadDayAsync(from, to, ct);
            if (input.Conversations.Count == 0)
            {
                return JobOutcome.Done;
            }

            var answer = LlmJson.Parse<DigestAnswer>(await llm.CompleteJsonAsync(
                new LlmRequest(
                    DigestPrompt.SchemaName, DigestPrompt.Schema,
                    DigestPrompt.System(llmOptions.CurrentValue.OutputLanguage, UserTimeZone.Name(zone)),
                    DigestPrompt.User(date, input, zone)),
                ct));
            await StoreAsync(payload, Validate(answer, input), ct);
            return JobOutcome.Done;
        }
        catch (Exception error) when (job.Attempts >= JobRunner.MaxAttempts && !ct.IsCancellationRequested)
        {
            // Only the third attempt gives up; the earlier ones throw and the runner tries again.
            logger.LogWarning("Making the digest failed after {Attempts} attempts: {Message}", job.Attempts, Describe(error));
            // The scheduler catches up yesterday too, so yesterday is retried as well; older dates are dropped.
            return date >= DigestDay.Today(time.GetUtcNow(), zone).AddDays(-1) ? JobOutcome.RunAgain(RetryAfter) : JobOutcome.Done;
        }
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The answer cut to length, its highlights kept to conversations of the input and empty items dropped.</summary>
    public static (string Headline, string Overview, DigestBody Body) Validate(DigestAnswer answer, DigestInput input)
    {
        var headline = ConversationPrompt.Cut(answer.Headline, DigestPrompt.MaxHeadline);
        var overview = ConversationPrompt.Cut(answer.Overview, DigestPrompt.MaxOverview);
        if (headline.Length == 0 || overview.Length == 0)
        {
            throw new LlmException("The language model endpoint answered with an empty digest.");
        }

        var known = input.Conversations.Select(c => c.Id).ToHashSet();
        var highlights = new List<DigestHighlight>();
        foreach (var highlight in answer.Highlights)
        {
            var text = ExtractMemoriesHandler.Cut(highlight.Text);
            if (text.Length > 0 && Guid.TryParse(highlight.ConversationId, out var id) && known.Contains(id))
            {
                highlights.Add(new DigestHighlight(text, id));
            }
        }

        return (headline, overview, new DigestBody(
            highlights.Take(DigestPrompt.MaxItems).ToList(), Items(answer.Decisions), Items(answer.OpenQuestions)));
    }

    private static List<string> Items(IReadOnlyList<string> items) =>
        items.Select(ExtractMemoriesHandler.Cut).Where(i => i.Length > 0).Take(DigestPrompt.MaxItems).ToList();

    private async Task StoreAsync(DigestPayload payload, (string Headline, string Overview, DigestBody Body) digest, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        if (await digests.InsertAsync(connection, transaction, id, payload.LocalDate, digest.Headline, digest.Overview, digest.Body, payload.Replace, now, ct))
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.DigestReady, id), connection, transaction, ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>A short reason for the log: a status, never a response body or digest text.</summary>
    private static string Describe(Exception error) => error switch
    {
        LlmException { StatusCode: { } code } => $"HTTP {code}",
        LlmException llm => llm.Message,
        OperationCanceledException or TimeoutException => "timeout",
        HttpRequestException => "connection refused",
        _ => "unexpected error",
    };
}
