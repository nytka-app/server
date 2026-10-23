using System.Text.Json;
using Nytka.Server.Jobs;
using Nytka.Server.Memories;
using Nytka.Server.Pipeline;
using Nytka.Storage;

namespace Nytka.Server.Ai.Backfill;

/// <summary>Where a backfill step stands: <c>done</c>, <c>pending</c> with the next request to answer, <c>busy</c> or <c>invalid</c>.</summary>
public sealed record BackfillStep(string Status, string? Key = null, LlmRequest? Request = null);

/// <summary>
/// Runs one kind of model work for one conversation with answers the owner's own model wrote (docs/how-to/backfill-with-claude.md):
/// <c>enrich</c> (title, summary, items, tags, again), <c>memories</c> (extraction, again) and <c>recheck</c> (which lines support
/// the memories taken before they cited lines). The job handlers run as they always do; only their model calls are answered from
/// <paramref name="answers"/>, and the first unanswered one is handed out. Nothing here calls a model.
/// </summary>
public sealed class BackfillService(IEnumerable<IJobHandler> handlers, MemoryRecheck recheck)
{
    public const string Enrich = "enrich";
    public const string Memories = "memories";
    public const string Recheck = "recheck";

    public static bool IsKind(string? kind) => kind is Enrich or Memories or Recheck;

    public async Task<BackfillStep> RunAsync(string kind, Guid conversationId, IReadOnlyDictionary<string, string> answers, CancellationToken ct)
    {
        var session = new BackfillSession(answers);
        BackfillLlmClient.Session = session;
        try
        {
            switch (kind)
            {
                case Enrich:
                    return Outcome(await Handler(JobKinds.EnrichConversation).RunAsync(
                        Job(JobKinds.EnrichConversation, new EnrichPayload(conversationId, Force: true, Again: true)), ct));
                case Memories:
                    return Outcome(await Handler(JobKinds.ExtractMemories).RunAsync(
                        Job(JobKinds.ExtractMemories, new ExtractPayload(conversationId, Again: true)), ct));
                default:
                    await recheck.RunAsync(conversationId, ct);
                    return new BackfillStep("done");
            }
        }
        catch (BackfillPendingException) when (session.Pending is { } request)
        {
            return new BackfillStep("pending", BackfillSession.KeyOf(request), request);
        }
        catch (LlmException)
        {
            // An answer that does not parse or breaks the schema: the caller asks its model again.
            return new BackfillStep("invalid");
        }
        finally
        {
            BackfillLlmClient.Session = null;
        }
    }

    private IJobHandler Handler(string kind) => handlers.Single(h => h.Kind == kind);

    private static JobRecord Job(string kind, object payload) => new(0, kind, JsonSerializer.Serialize(payload), 1);

    /// <summary>A job that would run again later (an open conversation, a batch still pending) is busy, never queued from here.</summary>
    private static BackfillStep Outcome(JobOutcome outcome) => new(outcome.RunAgainAfter is null ? "done" : "busy");
}
