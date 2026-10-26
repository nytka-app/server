using Nytka.Server.Auth;
using Nytka.Server.Privacy;
using Nytka.Server.Related;
using Nytka.Server.Search;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary><c>GET /api/v1/related</c>: the "Earlier" links of one conversation, task, memory or note.</summary>
public static class RelatedEndpoints
{
    public const int MaxLimit = 20;

    public static RouteGroupBuilder MapRelated(this RouteGroupBuilder api)
    {
        api.MapGet("/related", GetAsync).AllowRead();
        return api;
    }

    public sealed record RelatedPage(IReadOnlyList<RelatedLink> Items);

    /// <summary>Exactly one of <c>conversationId</c>, <c>taskId</c>, <c>memoryId</c> and <c>noteId</c>; <c>limit</c> 1 to <see cref="MaxLimit"/>, default 5.</summary>
    private static async Task<IResult> GetAsync(
        Guid? conversationId, Guid? taskId, Guid? memoryId, Guid? noteId, int? limit, RelatedStore related, EmbeddingState embeddings,
        SensitivityScope sensitivity, CancellationToken ct)
    {
        (string Kind, Guid? Id)[] given =
        [
            (RelatedKinds.Conversation, conversationId), (RelatedKinds.Task, taskId), (RelatedKinds.Memory, memoryId), (RelatedKinds.Note, noteId),
        ];
        var set = given.Where(g => g.Id is not null).ToList();
        if (set.Count != 1)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["id"] = ["Pass exactly one of conversationId, taskId, memoryId and noteId."],
            });
        }

        var take = Math.Clamp(limit ?? RelatedRule.MaxLinks, 1, MaxLimit);
        return Results.Ok(new RelatedPage(await RelatedLink.ForAsync(related, embeddings, set[0].Kind, set[0].Id!.Value, sensitivity.Hidden, take, ct)));
    }
}
