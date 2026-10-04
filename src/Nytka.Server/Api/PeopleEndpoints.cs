using System.Text.Json;
using Nytka.Server.Auth;
using Nytka.Server.Transcription;
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
    public const int MaxNoteLength = 500;
    public const int MaxSpeakerIdLength = 64;

    public static RouteGroupBuilder MapPeople(this RouteGroupBuilder api)
    {
        var people = api.MapGroup("/people");
        people.MapGet("", ListAsync).AllowRead();
        people.MapPost("", CreateAsync);
        people.MapPatch("/{id:guid}", UpdateAsync);
        people.MapDelete("/{id:guid}", DeleteAsync);
        people.MapPost("/{id:guid}/merge", MergeAsync);
        people.MapDelete("/{id:guid}/voices/{speakerId}", UnlinkAsync);
        api.MapGet("/voices", VoicesAsync).AllowRead();
        return api;
    }

    public const int MaxVoices = 50;

    public sealed record VoiceList(IReadOnlyList<UnnamedVoice> Items);

    /// <summary>Voices heard but not named, busiest first: the app offers them for naming.</summary>
    private static async Task<IResult> VoicesAsync(PeopleStore people, CancellationToken ct) =>
        Results.Ok(new VoiceList(await people.UnnamedVoicesAsync(MaxVoices, ct)));

    /// <summary>Body <c>{ intoId }</c>. Moves every voice of the person to <c>intoId</c> and deletes the person.</summary>
    private static async Task<IResult> MergeAsync(Guid id, HttpRequest http, PeopleStore people, CancellationToken ct)
    {
        var body = await ReadObjectAsync(http, ct);
        if (body is not { } json
            || !json.TryGetProperty("intoId", out var value)
            || value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var intoId))
        {
            return Invalid("intoId", "Must be the id of another person.");
        }

        if (intoId == id)
        {
            return Invalid("intoId", "Must differ from the person being merged.");
        }

        return await people.MergeAsync(id, intoId, ct) == PersonWrite.Ok
            ? Results.Ok(await people.GetAsync(intoId, ct))
            : NotFound();
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

    /// <summary>
    /// Body <c>{ name?, note? }</c>, at least one. <c>note: null</c> clears the note. A name another person has is a 409.
    /// </summary>
    private static async Task<IResult> UpdateAsync(Guid id, HttpRequest http, PeopleStore people, CancellationToken ct)
    {
        var body = await ReadObjectAsync(http, ct);
        if (body is not { } json)
        {
            return Invalid("name", $"Must be text of 1 to {MaxNameLength} characters.");
        }

        var hasName = json.TryGetProperty("name", out _);
        var name = Name(json);
        if (hasName && name is null)
        {
            return Invalid("name", $"Must be text of 1 to {MaxNameLength} characters.");
        }

        var hasNote = json.TryGetProperty("note", out var value);
        var note = hasNote && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;
        if (hasNote && (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || note is { Length: 0 or > MaxNoteLength }))
        {
            return Invalid("note", $"Must be text of 1 to {MaxNoteLength} characters, or null.");
        }

        if (!hasName && !hasNote)
        {
            return Invalid("name", "Give a name, a note or both.");
        }

        return await people.UpdateAsync(id, name, hasNote, note, ct) switch
        {
            PersonWrite.NotFound => NotFound(),
            PersonWrite.NameTaken => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Another person has that name."),
            _ => Results.Ok(await people.GetAsync(id, ct)),
        };
    }

    /// <summary>
    /// With <c>forget=true</c> the transcription service is also asked to delete the voiceprints of the person's voices;
    /// that answers 200 <c>{ forgotten }</c>, true only when every call succeeded. Otherwise 204.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid id, bool? forget, PeopleStore people, VoiceprintClient voiceprints, ILogger<VoiceprintClient> log, CancellationToken ct)
    {
        var voices = forget == true ? (await people.GetAsync(id, ct))?.Voices : null;
        if (!await people.DeleteAsync(id, ct))
        {
            return NotFound();
        }

        if (forget != true)
        {
            return Results.NoContent();
        }

        var forgotten = await voiceprints.ForgetAsync(voices ?? [], ct);
        log.LogInformation("Forgot the voiceprints of {Voices} voices: {Forgotten}", voices?.Length ?? 0, forgotten);
        return Results.Ok(new { forgotten });
    }

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
