using Nytka.Server.Auth;
using Nytka.Server.Privacy;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The note endpoint (docs/specs/task-kinds.md): the advice taken from summaries, one note per conversation and topic.</summary>
public static class NoteEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static RouteGroupBuilder MapNotes(this RouteGroupBuilder api)
    {
        api.MapGet("/notes", ListAsync).AllowRead();
        api.MapPatch("/notes/{id:guid}", PatchAsync);
        return api;
    }

    public sealed record NotePage(IReadOnlyList<NoteRow> Items, Guid? NextBefore);

    private static async Task<IResult> ListAsync(
        string? topic, Guid? conversationId, Guid? before, int? limit, NoteStore notes, SensitivityScope sensitivity, CancellationToken ct)
    {
        var normalized = TagName.Normalize(topic);
        if (topic is not null && normalized is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["topic"] = ["Must be a name of 1 to 32 letters, digits, - or _."] });
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var items = await notes.ListAsync(normalized, conversationId, before, take, ct, sensitivity.Hidden);
        return Results.Ok(new NotePage(items, items.Count == take ? items[^1].Id : null));
    }

    /// <summary>Body <c>{ salience }</c>: the wearer's mark on how much the note matters, or null to take it back. A summary never overwrites a mark.</summary>
    private static async Task<IResult> PatchAsync(Guid id, HttpRequest http, NoteStore notes, TimeProvider time, CancellationToken ct)
    {
        var body = await SalienceBody.ReadAsync(http, ct);
        if (!SalienceBody.TryRead(body, out var salience) || salience is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["salience"] = [SalienceBody.Bad] });
        }

        return await notes.SetSalienceAsync(id, salience.Salience, time.GetUtcNow(), ct) && await notes.GetAsync(id, ct) is { } note
            ? Results.Ok(note)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such note.");
    }
}
