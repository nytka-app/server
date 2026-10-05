using System.Text.Json;
using Nytka.Server.Auth;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The tag endpoints (docs/specs/tags.md): the tags in use, and rename, merge and delete for the whole server. Adding or
/// removing a tag on one item is under <c>/conversations</c> and <c>/people</c>. Listing needs <c>read</c>; every change
/// needs <c>admin</c>. A tag name is the owner's word about a person or a moment: no problem title, log line or error
/// holds one.
/// </summary>
public static class TagEndpoints
{
    public static RouteGroupBuilder MapTags(this RouteGroupBuilder api)
    {
        var tags = api.MapGroup("/tags");
        tags.MapGet("", ListAsync).AllowRead();
        tags.MapPost("/{name}/rename", RenameAsync);
        tags.MapPost("/{name}/merge", MergeAsync);
        tags.MapDelete("/{name}", DeleteAsync);
        return api;
    }

    public sealed record TagList(IReadOnlyList<TagCount> Items);

    /// <summary>The tags of an item after an add or a remove.</summary>
    public sealed record ItemTags(IReadOnlyList<string> Tags);

    /// <summary>Tags in use, most used first. <c>q</c> keeps names that start with it, normalized as a name is.</summary>
    private static async Task<IResult> ListAsync(string? q, TagStore tags, CancellationToken ct) =>
        Results.Ok(await ListForAsync(q, tags, ct));

    /// <summary>Shared with the MCP tool, so both return the same tags.</summary>
    public static async Task<TagList> ListForAsync(string? q, TagStore tags, CancellationToken ct)
    {
        var prefix = string.IsNullOrWhiteSpace(q) ? null : TagName.Normalize(q);
        return string.IsNullOrWhiteSpace(q) || prefix is not null
            ? new TagList(await tags.ListAsync(prefix, ct))
            : new TagList([]);
    }

    /// <summary>Body <c>{ name }</c>. 200 with the tag; 404; 409 when the new name is in use by another tag.</summary>
    private static async Task<IResult> RenameAsync(string name, HttpRequest http, TagStore tags, CancellationToken ct)
    {
        if (TagName.Normalize(name) is not { } old)
        {
            return InvalidName();
        }

        var body = await PeopleEndpoints.ReadObjectAsync(http, ct);
        if (BodyName(body, "name") is not { } renamed)
        {
            return InvalidName();
        }

        return await tags.RenameAsync(old, renamed, ct) switch
        {
            TagWrite.NotFound => NotFound(),
            TagWrite.NameTaken => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Another tag has that name. Merge instead."),
            _ => Results.Ok(await tags.GetAsync(renamed, ct)),
        };
    }

    /// <summary>Body <c>{ into }</c>. 200 with the target tag; 404 for an unknown tag.</summary>
    private static async Task<IResult> MergeAsync(string name, HttpRequest http, TagStore tags, TimeProvider time, CancellationToken ct)
    {
        if (TagName.Normalize(name) is not { } from)
        {
            return InvalidName();
        }

        var body = await PeopleEndpoints.ReadObjectAsync(http, ct);
        if (BodyName(body, "into") is not { } into)
        {
            return PeopleEndpoints.Invalid("into", InvalidNameSentence);
        }

        return await tags.MergeAsync(from, into, time.GetUtcNow(), ct) is { } merged ? Results.Ok(merged) : NotFound();
    }

    private static async Task<IResult> DeleteAsync(string name, TagStore tags, CancellationToken ct) =>
        TagName.Normalize(name) is not { } normalized
            ? InvalidName()
            : await tags.DeleteAsync(normalized, ct) ? Results.NoContent() : NotFound();

    private static string? BodyName(JsonElement? body, string property) =>
        body is { } json && json.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? TagName.Normalize(value.GetString())
            : null;

    internal const string InvalidNameSentence = "Must be 1 to 32 letters, digits, - or _, starting with a letter or digit.";

    /// <summary>For a <c>tag</c> filter that is no valid name; a fixed sentence, never the tag.</summary>
    internal const string InvalidTagSentence = "tag must be 1 to 32 letters, digits, - or _, starting with a letter or digit.";

    internal static IResult Invalid(string field) => PeopleEndpoints.Invalid(field, InvalidNameSentence);

    internal static IResult InvalidName() => PeopleEndpoints.Invalid("name", InvalidNameSentence);

    internal static IResult TooMany() =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"This item has {TagStore.MaxPerItem} tags.");

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such tag.");
}
