using Nytka.Server.Auth;
using Nytka.Server.Search;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The search endpoint (docs/specs/v0.4.md, track G).</summary>
public static class SearchEndpoints
{
    public sealed record Hit(
        string Kind, Guid Id, float Score, string? Title, string Snippet, DateTime At, Guid? ConversationId);

    public sealed record HitPage(IReadOnlyList<Hit> Items, int? NextOffset);

    public static RouteGroupBuilder MapSearch(this RouteGroupBuilder api)
    {
        api.MapGet("/search", SearchAsync).AllowRead();
        return api;
    }

    private static async Task<IResult> SearchAsync(
        string? q, string? kinds, int? limit, int? offset, SearchStore search, CancellationToken ct)
    {
        if (SearchQuery.Parse(q, kinds is null ? null : [kinds], out var error) is not { } query)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error);
        }

        var page = await RunAsync(search, query, limit ?? SearchQuery.DefaultLimit, offset ?? 0, ct);
        return Results.Ok(page);
    }

    /// <summary>Shared with the MCP tool, so both return the same hits.</summary>
    public static async Task<HitPage> RunAsync(SearchStore search, SearchQuery query, int limit, int offset, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, SearchQuery.MaxLimit);
        var skip = Math.Clamp(offset, 0, SearchQuery.MaxOffset);
        var rows = await search.SearchAsync(query.Terms, query.Conversations, query.Memories, take + 1, skip, ct);
        var items = rows.Take(take)
            .Select(r => new Hit(r.Kind, r.Id, r.Score, r.Title, r.Snippet, r.At, r.ConversationId))
            .ToList();
        var next = rows.Count > take && skip + take <= SearchQuery.MaxOffset ? skip + take : (int?)null;
        return new HitPage(items, next);
    }
}
