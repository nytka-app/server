using Nytka.Server.Auth;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// Topic pages (docs/explanation/features.md#topic-pages): one running page per recurring tag or named person, with its
/// conversations newest first and its open commitments. Read scope; the pages are made by <c>fold-topic</c>.
/// </summary>
public static class TopicEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int TimelineLimit = 30;
    public const int MaxTimeline = 100;
    public const int MaxCommitments = 50;

    public static RouteGroupBuilder MapTopics(this RouteGroupBuilder api)
    {
        var topics = api.MapGroup("/topics");
        topics.MapGet("", ListAsync).AllowRead();
        topics.MapGet("/{id:guid}", GetAsync).AllowRead();
        return api;
    }

    public sealed record TopicList(IReadOnlyList<TopicRow> Items);

    public sealed record TopicPage(
        Guid Id, string Kind, string Name, Guid? TagId, Guid? PersonId, string? Summary, int Moments, int OpenCommitments, DateTime? LastAt,
        DateTime UpdatedAt, IReadOnlyList<TopicMoment> Timeline, DateTime? NextBefore, IReadOnlyList<TaskRow> Commitments);

    /// <summary><c>kind</c> is <c>tag</c> or <c>person</c>; <c>q</c> keeps names that start with it.</summary>
    private static async Task<IResult> ListAsync(string? kind, string? q, int? limit, TopicStore topics, CancellationToken ct)
    {
        if (kind is not (null or TopicStore.Tag or TopicStore.Person))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Must be tag or person."] });
        }

        return Results.Ok(new TopicList(await topics.ListAsync(
            kind, string.IsNullOrWhiteSpace(q) ? null : q.Trim(), Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct)));
    }

    /// <summary>The page; <c>before</c> (an instant) and <c>limit</c> page the timeline, newest first.</summary>
    private static async Task<IResult> GetAsync(
        Guid id, DateTimeOffset? before, int? limit, TopicStore topics, TaskStore tasks, CancellationToken ct) =>
        await PageAsync(id, before, limit, topics, tasks, ct) is { } page
            ? Results.Ok(page)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such topic.");

    /// <summary>Shared with the MCP tool, so both return the same page; null when there is no such topic.</summary>
    public static async Task<TopicPage?> PageAsync(
        Guid id, DateTimeOffset? before, int? limit, TopicStore topics, TaskStore tasks, CancellationToken ct)
    {
        if (await topics.GetAsync(id, ct) is not { } topic)
        {
            return null;
        }

        var take = Math.Clamp(limit ?? TimelineLimit, 1, MaxTimeline);
        var timeline = await topics.TimelineAsync(id, before?.ToUniversalTime(), take, ct);
        var commitments = new List<TaskRow>();
        foreach (var taskId in await topics.OpenCommitmentsAsync(id, MaxCommitments, ct))
        {
            if (await tasks.GetAsync(taskId, ct) is { } task)
            {
                commitments.Add(task);
            }
        }

        return new TopicPage(
            topic.Id, topic.Kind, topic.Name, topic.TagId, topic.PersonId, topic.Summary, topic.Moments, topic.OpenCommitments, topic.LastAt,
            topic.UpdatedAt, timeline, timeline.Count == take ? timeline[^1].StartedAt : null, commitments);
    }
}
