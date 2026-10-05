using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Calendar;

/// <summary>
/// The <c>make-brief</c> job (docs/specs/people.md, Pre-meeting brief): one model call for one occurrence of an event that has a
/// matched person, from their facts, note, open tasks and the titles and summaries of their last conversations (never a
/// transcript), stored with its <c>brief.ready</c> event in one transaction. The job runner makes the three attempts; after the
/// third the failure is logged here and the job comes back in ten minutes until the meeting starts.
/// </summary>
public sealed class MakeBriefHandler(
    ILlmClient llm,
    IOptionsMonitor<LlmOptions> llmOptions,
    SettingsService settings,
    CalendarStore calendar,
    PersonFactStore facts,
    PeopleStore people,
    NpgsqlDataSource dataSource,
    IEventPublisher events,
    TimeProvider time,
    ILogger<MakeBriefHandler> logger)
    : IJobHandler
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    public string Kind => JobKinds.MakeBrief;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<BriefPayload>(job.Payload)!;
        var now = time.GetUtcNow();
        if (!llm.IsConfigured
            || await calendar.GetAsync(payload.Uid, payload.StartsAt, ct) is not { } meeting
            || payload.StartsAt <= now
            || await calendar.HasBriefAsync(payload.Uid, payload.StartsAt, ct)
            || CalendarPeople.Match(meeting.Attendees, await facts.PeopleAsync(ct)) is not { Count: > 0 } matched)
        {
            return JobOutcome.Done;
        }

        try
        {
            var zone = UserTimeZone.Resolve(settings);
            var going = new List<BriefPerson>();
            foreach (var person in matched)
            {
                if (await people.ViewAsync(person.Id, ct) is { } view)
                {
                    going.Add(new BriefPerson(
                        view.Name, view.Note, view.Facts.Select(f => f.Text).ToList(), view.OpenTasks.Select(t => t.Text).ToList(),
                        await calendar.RecentConversationsAsync(person.Id, BriefPrompt.MaxConversations, ct)));
                }
            }

            if (going.Count == 0)
            {
                return JobOutcome.Done;
            }

            var answer = LlmJson.Parse<BriefAnswer>(await llm.CompleteJsonAsync(
                new LlmRequest(
                    BriefPrompt.SchemaName, BriefPrompt.Schema,
                    BriefPrompt.System(llmOptions.CurrentValue.OutputLanguage, UserTimeZone.Name(zone)),
                    BriefPrompt.User(meeting.Title, payload.StartsAt, going, zone)),
                ct));
            var text = ConversationPrompt.Cut(answer.Text, BriefPrompt.MaxText);
            if (text.Length == 0)
            {
                throw new LlmException("The language model endpoint answered with an empty brief.");
            }

            await StoreAsync(payload, matched.Select(p => p.Id).ToList(), text, ct);
            return JobOutcome.Done;
        }
        catch (Exception error) when (job.Attempts >= JobRunner.MaxAttempts && !ct.IsCancellationRequested)
        {
            // Only the third attempt gives up; the earlier ones throw and the runner tries again.
            logger.LogWarning("Making a brief failed after {Attempts} attempts: {Message}", job.Attempts, Describe(error));
            return payload.StartsAt > now + RetryAfter ? JobOutcome.RunAgain(RetryAfter) : JobOutcome.Done;
        }
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    private async Task StoreAsync(BriefPayload payload, IReadOnlyCollection<Guid> personIds, string text, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        if (await calendar.InsertBriefAsync(connection, transaction, id, payload.Uid, payload.StartsAt, personIds, text, now, ct))
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.BriefReady, id), connection, transaction, ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>A short reason for the log: a status, never a response body, a title or a name.</summary>
    private static string Describe(Exception error) => error switch
    {
        LlmException { StatusCode: { } code } => $"HTTP {code}",
        LlmException llm => llm.Message,
        OperationCanceledException or TimeoutException => "timeout",
        HttpRequestException => "connection refused",
        _ => "unexpected error",
    };
}
