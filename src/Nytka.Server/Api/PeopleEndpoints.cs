using System.Text.Json;
using Nytka.Server.Auth;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The people endpoints (docs/specs/v0.6.md): name the voices a transcription provider tells apart. A voice is
/// the provider's <c>speaker_id</c>; segments show the name when they are read, so naming a voice renames its
/// past segments too. Listing needs <c>read</c>; every change needs <c>admin</c>.
/// </summary>
public static class PeopleEndpoints
{
    public const int MaxNameLength = 80;
    public const int MaxSpeakerIdLength = 64;

    public static RouteGroupBuilder MapPeople(this RouteGroupBuilder api)
    {
        var people = api.MapGroup("/people");
        people.MapGet("", ListAsync).AllowRead();
        people.MapPost("", CreateAsync);
        people.MapPatch("/{id:guid}", RenameAsync);
        people.MapDelete("/{id:guid}", DeleteAsync);
        people.MapDelete("/{id:guid}/voices/{speakerId}", UnlinkAsync);
        return api;
    }

    public sealed record PersonList(IReadOnlyList<PersonRow> Items);

    private static async Task<IResult> ListAsync(PeopleStore people, CancellationToken ct) =>
        Results.Ok(new PersonList(await people.ListAsync(ct)));

    /// <summary>
    /// Body <c>{ name, speakerId? }</c>. With a <c>speakerId</c> the voice takes the name: the person who has it (any
    /// case) when there is one, else a new person. Answers 201 for a new person and 200 for an existing one.
    /// </summary>
    private static async Task<IResult> CreateAsync(HttpRequest http, PeopleStore people, TimeProvider time, CancellationToken ct)
    {
        var body = await ReadObjectAsync(http, ct);
        if (body is not { } json)
        {
            return Invalid("body", "Must be a JSON object.");
        }

        if (Name(json) is not { } name)
        {
            return Invalid("name", $"Must be text of 1 to {MaxNameLength} characters.");
        }

        var before = (await people.ListAsync(ct)).Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Guid id;
        if (json.TryGetProperty("speakerId", out var voice) && voice.ValueKind != JsonValueKind.Null)
        {
            if (voice.ValueKind != JsonValueKind.String || voice.GetString()!.Trim() is not { Length: > 0 and <= MaxSpeakerIdLength } speakerId)
            {
                return Invalid("speakerId", $"Must be text of 1 to {MaxSpeakerIdLength} characters, or null.");
            }

            id = await people.NameVoiceAsync(name, speakerId, time.GetUtcNow(), ct);
        }
        else
        {
            id = await people.CreateAsync(name, time.GetUtcNow(), ct);
        }

        var person = await people.GetAsync(id, ct);
        return before ? Results.Ok(person) : Results.Created($"/api/v1/people/{id}", person);
    }

    /// <summary>Body <c>{ name }</c>. A name another person has is a 409.</summary>
    private static async Task<IResult> RenameAsync(Guid id, HttpRequest http, PeopleStore people, CancellationToken ct)
    {
        var body = await ReadObjectAsync(http, ct);
        if (body is not { } json || Name(json) is not { } name)
        {
            return Invalid("name", $"Must be text of 1 to {MaxNameLength} characters.");
        }

        return await people.RenameAsync(id, name, ct) switch
        {
            PersonWrite.NotFound => NotFound(),
            PersonWrite.NameTaken => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Another person has that name."),
            _ => Results.Ok(await people.GetAsync(id, ct)),
        };
    }

    private static async Task<IResult> DeleteAsync(Guid id, PeopleStore people, CancellationToken ct) =>
        await people.DeleteAsync(id, ct) ? Results.NoContent() : NotFound();

    private static async Task<IResult> UnlinkAsync(Guid id, string speakerId, PeopleStore people, CancellationToken ct) =>
        await people.UnlinkVoiceAsync(id, speakerId, ct) ? Results.NoContent() : NotFound();

    private static string? Name(JsonElement body)
    {
        if (!body.TryGetProperty("name", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var name = value.GetString()!.Trim();
        return name.Length is > 0 and <= MaxNameLength ? name : null;
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

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such person.");
}
