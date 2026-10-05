using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Speech;
using Nytka.Storage;

namespace Nytka.Server.Api;

public static class ConversationEndpoints
{
    public const int DefaultLimit = 30;
    public const int MaxLimit = 100;
    public const int PreviewLength = 140;
    public const int MaxTitleLength = 120;

    public static RouteGroupBuilder MapConversations(this RouteGroupBuilder api)
    {
        var conversations = api.MapGroup("/conversations");
        conversations.MapGet("", ListAsync).AllowRead();
        conversations.MapGet("/{id:guid}", GetAsync).AllowRead();
        conversations.MapPatch("/{id:guid}", PatchAsync);
        conversations.MapPost("/{id:guid}/enrich", EnrichAsync);
        conversations.MapPost("/{id:guid}/speech", MarkSpeechAsync);
        conversations.MapDelete("/{id:guid}", DeleteAsync);
        conversations.MapPut("/{id:guid}/tags/{name}", AddTagAsync);
        conversations.MapDelete("/{id:guid}/tags/{name}", RemoveTagAsync);
        conversations.MapGet("/{id:guid}/transcriptions", TranscriptionsAsync);
        return api;
    }

    public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, DateTime? NextBefore);

    public sealed record ConversationDetail(
        Guid Id, DateTime StartedAt, DateTime EndedAt, string Status, string? Title, string? Summary, string AiStatus,
        bool TitleEdited, string? AiMessage, DateTime? AiUpdatedAt, IReadOnlyList<TaskRow> Tasks, IReadOnlyList<SegmentRow> Segments,
        IReadOnlyList<BookmarkRef> Bookmarks, string Source, IReadOnlyList<string> Tags);

    public sealed record EnrichResponse(string AiStatus);

    public sealed record SpeechMarked(int Marked);

    public sealed record TranscriptionView(
        long Id, DateTime StartedAt, DateTime EndedAt, string Status, string? Error, JsonElement? Response);

    /// <summary><c>media</c> is <c>hide</c> (leave out conversations that are mostly media) or <c>only</c> (keep just those), else <c>400</c>.</summary>
    private static async Task<IResult> ListAsync(
        DateTimeOffset? before, DateTimeOffset? since, int? limit, string? tag, string? media, ConversationStore conversations, CancellationToken ct)
    {
        var normalized = TagName.Normalize(tag);
        if (tag is not null && normalized is null)
        {
            return TagEndpoints.Invalid("tag");
        }

        if (media is not (null or "hide" or "only"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["media"] = ["Must be hide or only, or left out."] });
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        // Npgsql takes only UTC offsets for timestamptz.
        var items = (await conversations.ListAsync(before?.ToUniversalTime(), since?.ToUniversalTime(), take, normalized, media, ct))
            .Select(c => c.Preview.Length > PreviewLength ? c with { Preview = c.Preview[..(char.IsHighSurrogate(c.Preview[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength)] } : c)
            .ToList();
        return Results.Ok(new ConversationPage(items, items.Count == take ? items[^1].StartedAt : null));
    }

    private static async Task<IResult> GetAsync(
        Guid id, ConversationStore conversations, TaskStore tasks, BookmarkStore bookmarks, TagStore tags, CancellationToken ct) =>
        await DetailAsync(id, conversations, tasks, bookmarks, tags, ct);

    private static async Task<IResult> DetailAsync(
        Guid id, ConversationStore conversations, TaskStore tasks, BookmarkStore bookmarks, TagStore tags, CancellationToken ct)
    {
        if (await conversations.GetAsync(id, ct) is not { } conversation)
        {
            return NotFound();
        }

        var segments = await conversations.SegmentsAsync(id, ct);
        return Results.Ok(new ConversationDetail(
            conversation.Id, conversation.StartedAt, conversation.EndedAt, conversation.Status, conversation.Title,
            conversation.Summary, conversation.AiStatus, conversation.TitleEdited, conversation.AiMessage,
            conversation.AiUpdatedAt, await tasks.ForConversationAsync(id, ct), segments,
            await bookmarks.ForConversationAsync(id, ct), conversation.Source, await tags.OfConversationAsync(id, ct)));
    }

    /// <summary>Body <c>{ title }</c>: 1 to 120 characters, or null for the generated title.</summary>
    private static async Task<IResult> PatchAsync(
        Guid id, JsonElement body, ConversationStore conversations, TaskStore tasks, BookmarkStore bookmarks, TagStore tags,
        TimeProvider time, CancellationToken ct)
    {
        string? title = null;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("title", out var value))
        {
            return Invalid("Send a title, or null for the generated one.");
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            title = value.GetString()!.Trim();
            if (title.Length is 0 or > MaxTitleLength)
            {
                return Invalid($"The title must be 1 to {MaxTitleLength} characters.");
            }
        }
        else if (value.ValueKind != JsonValueKind.Null)
        {
            return Invalid("The title must be a string or null.");
        }

        return await conversations.SetTitleAsync(id, title, time.GetUtcNow(), ct)
            ? await DetailAsync(id, conversations, tasks, bookmarks, tags, ct)
            : NotFound();
    }

    /// <summary>Queues a run that ignores "nothing new" and resets the failure count.</summary>
    private static async Task<IResult> EnrichAsync(
        Guid id, ConversationStore conversations, EnrichmentQueue enrichments, ILlmClient llm, CancellationToken ct)
    {
        if (await conversations.GetAsync(id, ct) is not { } conversation)
        {
            return NotFound();
        }

        if (conversation.Status != "closed")
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The conversation is still open.");
        }

        if (!llm.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The language model is not configured.");
        }

        return await enrichments.QueueAsync(id, force: true, ct)
            ? Results.Accepted(value: new EnrichResponse("pending"))
            : NotFound();
    }

    /// <summary>
    /// Body <c>{ kind }</c>: <c>person</c>, <c>media</c>, <c>call</c>, or null to clear. Marks every line of the conversation that is not
    /// the wearer's, in every mode; <c>200 { marked }</c> counts them.
    /// </summary>
    private static async Task<IResult> MarkSpeechAsync(
        Guid id, HttpRequest http, ConversationStore conversations, SpeechStore speech, CancellationToken ct)
    {
        if (await PeopleEndpoints.ReadObjectAsync(http, ct) is not { } body || !body.TryGetProperty("kind", out var value)
            || !SpeechBody.TryKind(value, out var kind))
        {
            return PeopleEndpoints.Invalid("kind", SpeechBody.BadKind);
        }

        return await conversations.GetAsync(id, ct) is null
            ? NotFound()
            : Results.Ok(new SpeechMarked(await speech.MarkConversationAsync(id, kind, ct)));
    }

    private static IResult Invalid(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [message] });

    /// <summary>Adds the tag, created when new. 200 <c>{ tags }</c>, also when the conversation had it; 400 for a bad name, 404, 409 at 20 tags.</summary>
    private static async Task<IResult> AddTagAsync(Guid id, string name, TagStore tags, TimeProvider time, CancellationToken ct)
    {
        if (TagName.Normalize(name) is not { } normalized)
        {
            return TagEndpoints.InvalidName();
        }

        return await tags.AddToConversationAsync(id, normalized, time.GetUtcNow(), ct) switch
        {
            TagAdd.NoItem => NotFound(),
            TagAdd.TooMany => TagEndpoints.TooMany(),
            _ => Results.Ok(new TagEndpoints.ItemTags(await tags.OfConversationAsync(id, ct))),
        };
    }

    /// <summary>Removes the tag. 200 <c>{ tags }</c>, also when the conversation did not have it; 400 for a bad name, 404.</summary>
    private static async Task<IResult> RemoveTagAsync(Guid id, string name, TagStore tags, CancellationToken ct) =>
        TagName.Normalize(name) is not { } normalized
            ? TagEndpoints.InvalidName()
            : await tags.RemoveFromConversationAsync(id, normalized, ct)
                ? Results.Ok(new TagEndpoints.ItemTags(await tags.OfConversationAsync(id, ct)))
                : NotFound();

    private static async Task<IResult> DeleteAsync(Guid id, ConversationStore conversations, CancellationToken ct) =>
        await conversations.DeleteAsync(id, ct) ? Results.NoContent() : NotFound();

    private static async Task<IResult> TranscriptionsAsync(
        Guid id, ConversationStore conversations, BatchStore batches, CancellationToken ct)
    {
        if (await conversations.GetAsync(id, ct) is null)
        {
            return NotFound();
        }

        var rows = await batches.ListForConversationAsync(id, ct);
        return Results.Ok(rows.Select(b => new TranscriptionView(
            b.Id, b.StartedAt, b.EndedAt, b.Status, b.Error,
            b.Response is null ? null : JsonSerializer.Deserialize<JsonElement>(b.Response))));
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such conversation.");
}
