using System.Text.Json;
using Npgsql;
using Nytka.Server.Auth;
using Nytka.Server.Events;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The bookmark endpoints (docs/specs/v0.8.md): moments the wearer marked. Listing needs <c>read</c>; every change
/// needs <c>admin</c>. A bookmark holds no conversation id: <c>conversationId</c> is worked out from its time.
/// </summary>
public static class BookmarkEndpoints
{
    public const int DefaultLimit = 30;
    public const int MaxLimit = 100;
    public const int MaxNoteLength = 200;

    public static RouteGroupBuilder MapBookmarks(this RouteGroupBuilder api)
    {
        var bookmarks = api.MapGroup("/bookmarks");
        bookmarks.MapGet("", ListAsync).AllowRead();
        bookmarks.MapPost("", CreateAsync);
        bookmarks.MapPatch("/{id:guid}", PatchAsync);
        bookmarks.MapDelete("/{id:guid}", DeleteAsync);
        return api;
    }

    public sealed record BookmarkPage(IReadOnlyList<BookmarkRow> Items, DateTime? NextBefore);

    private static async Task<IResult> ListAsync(DateTimeOffset? before, int? limit, BookmarkStore bookmarks, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        // Npgsql takes only UTC offsets for timestamptz.
        var items = await bookmarks.ListAsync(before?.ToUniversalTime(), take, ct);
        return Results.Ok(new BookmarkPage(items, items.Count == take ? items[^1].At : null));
    }

    /// <summary>
    /// Body <c>{ id, at, note?, source }</c>. Answers 201 for a new bookmark and 200, unchanged, when the id exists, so an
    /// upload the app retries is a no-op. <c>bookmark.created</c> is published in the insert's transaction.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        HttpRequest http, NpgsqlDataSource dataSource, BookmarkStore bookmarks, IEventPublisher events, TimeProvider time,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var body = await ReadObjectAsync(http, ct);
        Guid id = default;
        DateTimeOffset at = default;
        string? note = null;
        string? source = null;
        if (body is not { } json)
        {
            errors["body"] = ["Must be a JSON object."];
        }
        else
        {
            if (!json.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String || !Guid.TryParse(idValue.GetString(), out id))
            {
                errors["id"] = ["Must be a UUID."];
            }

            if (!json.TryGetProperty("at", out var atValue) || atValue.ValueKind != JsonValueKind.String || !atValue.TryGetDateTimeOffset(out at))
            {
                errors["at"] = ["Must be an ISO 8601 time with an offset."];
            }

            if (json.TryGetProperty("source", out var sourceValue) && sourceValue.ValueKind == JsonValueKind.String
                && sourceValue.GetString() is "pendant" or "app")
            {
                source = sourceValue.GetString();
            }
            else
            {
                errors["source"] = ["Must be pendant or app."];
            }

            if (json.TryGetProperty("note", out var noteValue) && !ReadNote(noteValue, out note))
            {
                errors["note"] = [NoteMessage];
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var now = time.GetUtcNow();
        var created = await bookmarks.AddAsync(connection, transaction, id, at.ToUniversalTime(), note, source!, now, ct);
        if (created)
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.BookmarkCreated, id), connection, transaction, ct);
        }

        await transaction.CommitAsync(ct);
        var row = await bookmarks.GetAsync(id, ct);
        return created ? Results.Created($"/api/v1/bookmarks/{id}", row) : Results.Ok(row);
    }

    /// <summary>Body <c>{ note }</c>: 1 to 200 characters, or null (or blank) to clear it.</summary>
    private static async Task<IResult> PatchAsync(Guid id, HttpRequest http, BookmarkStore bookmarks, CancellationToken ct)
    {
        var body = await ReadObjectAsync(http, ct);
        if (body is not { } json || !json.TryGetProperty("note", out var value) || !ReadNote(value, out var note))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["note"] = [NoteMessage] });
        }

        return await bookmarks.SetNoteAsync(id, note, ct) ? Results.Ok(await bookmarks.GetAsync(id, ct)) : NotFound();
    }

    private static async Task<IResult> DeleteAsync(Guid id, BookmarkStore bookmarks, CancellationToken ct) =>
        await bookmarks.DeleteAsync(id, ct) ? Results.NoContent() : NotFound();

    private static readonly string NoteMessage = $"Must be text of up to {MaxNoteLength} characters, or null.";

    /// <summary>The trimmed note, null when absent or blank; false when it is not a string or is too long.</summary>
    private static bool ReadNote(JsonElement value, out string? note)
    {
        note = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString()!.Trim();
        if (text.EnumerateRunes().Count() > MaxNoteLength)
        {
            return false;
        }

        note = text.Length == 0 ? null : text;
        return true;
    }

    private static async Task<JsonElement?> ReadObjectAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            var body = await request.ReadFromJsonAsync<JsonElement>(ct);
            return body.ValueKind == JsonValueKind.Object ? body : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such bookmark.");
}
