using System.Text.Json;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Events;
using Nytka.Server.People;
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
        people.MapGet("/{id:guid}", ViewAsync).AllowRead();
        people.MapPost("", CreateAsync);
        people.MapPatch("/{id:guid}", UpdateAsync);
        people.MapDelete("/{id:guid}", DeleteAsync);
        people.MapPost("/{id:guid}/merge", MergeAsync);
        people.MapGet("/suggestions", SuggestionsAsync).AllowRead();
        people.MapPost("/backfill", BackfillAsync);
        people.MapPost("/suggestions/{id:guid}/accept", AcceptSuggestionAsync);
        people.MapPost("/suggestions/{id:guid}/reject", RejectSuggestionAsync);
        people.MapDelete("/{id:guid}/voices/{speakerId}", UnlinkAsync);
        people.MapPut("/{id:guid}/tags/{name}", AddTagAsync);
        people.MapDelete("/{id:guid}/tags/{name}", RemoveTagAsync);
        people.MapGet("/{id:guid}/facts", FactsAsync).AllowRead();
        people.MapPost("/{id:guid}/facts", AddFactAsync);
        people.MapPatch("/{id:guid}/facts/{factId:guid}", EditFactAsync);
        people.MapDelete("/{id:guid}/facts/{factId:guid}", DeleteFactAsync);
        api.MapGet("/voices", VoicesAsync).AllowRead();
        return api;
    }

    public const int DefaultBackfillLimit = 200;
    public const int MaxBackfillLimit = 1000;
    public const int MaxVoices = 50;
    public const int MaxSuggestions = 200;
    public const int DefaultFactLimit = 50;
    public const int MaxFactLimit = 200;

    /// <summary>
    /// Queues name suggestions and fact extraction for summarized conversations the People features have not read, newest first,
    /// at most <c>limit</c> (default 200, up to 1000). 200 with <c>{ queued: { suggestNames, facts }, skipped, remaining }</c>;
    /// 409 without a language model. Enrichment does not run again.
    /// </summary>
    private static async Task<IResult> BackfillAsync(int? limit, PeopleBackfill backfill, ILlmClient llm, CancellationToken ct)
    {
        if (limit is < 1 or > MaxBackfillLimit)
        {
            return Invalid("limit", $"Must be 1 to {MaxBackfillLimit}.");
        }

        return llm.IsConfigured
            ? Results.Ok(await backfill.RunAsync(limit ?? DefaultBackfillLimit, ct))
            : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "No language model is configured.");
    }

    private static async Task<IResult> FactsAsync(Guid id, Guid? before, int? limit, PersonFactStore facts, CancellationToken ct) =>
        await facts.ListAsync(id, before, Math.Clamp(limit ?? DefaultFactLimit, 1, MaxFactLimit), ct) is { } page
            ? Results.Ok(page)
            : NotFound();

    /// <summary>Body <c>{ text }</c>. 201 with the fact (<c>source: user</c>, no basis); 409 when a live fact of the person holds it.</summary>
    private static async Task<IResult> AddFactAsync(
        Guid id, HttpRequest http, NpgsqlDataSource dataSource, PersonFactStore facts, IEventPublisher events, TimeProvider time,
        CancellationToken ct)
    {
        var valid = MemoryEndpoints.ValidateText(await TokenEndpoints.ReadBodyAsync<MemoryEndpoints.TextRequest>(http, ct));
        if (valid.Problem is not null)
        {
            return valid.Problem;
        }

        if (!await facts.ExistsAsync(id, ct))
        {
            return NotFound();
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await facts.AddAsync(connection, transaction, id, valid.Text!, valid.Fingerprint!, time.GetUtcNow(), ct) is not { } factId)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A fact of this person already holds this.");
        }

        await events.PublishAsync(new NytkaEvent(NytkaEvent.PersonFactCreated, factId), connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return Results.Json(await facts.GetAsync(id, factId, ct), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> EditFactAsync(
        Guid id, Guid factId, HttpRequest http, PersonFactStore facts, TimeProvider time, CancellationToken ct)
    {
        var valid = MemoryEndpoints.ValidateText(await TokenEndpoints.ReadBodyAsync<MemoryEndpoints.TextRequest>(http, ct));
        if (valid.Problem is not null)
        {
            return valid.Problem;
        }

        return await facts.UpdateTextAsync(id, factId, valid.Text!, time.GetUtcNow(), ct) && await facts.GetAsync(id, factId, ct) is { } fact
            ? Results.Ok(fact)
            : FactNotFound();
    }

    private static async Task<IResult> DeleteFactAsync(Guid id, Guid factId, PersonFactStore facts, TimeProvider time, CancellationToken ct) =>
        await facts.DeleteAsync(id, factId, time.GetUtcNow(), ct) ? Results.NoContent() : FactNotFound();

    public sealed record VoiceList(IReadOnlyList<UnnamedVoice> Items);

    /// <summary>Voices heard but not named, busiest first: the app offers them for naming.</summary>
    private static async Task<IResult> VoicesAsync(PeopleStore people, CancellationToken ct) =>
        Results.Ok(new VoiceList(await people.UnnamedVoicesAsync(MaxVoices, ct)));

    public sealed record SuggestionList(IReadOnlyList<NameSuggestionRow> Items);

    /// <summary>Names the model suggested for unnamed voices, newest first, at most 200. <c>status</c> is <c>pending</c> (default), <c>accepted</c> or <c>rejected</c>.</summary>
    private static async Task<IResult> SuggestionsAsync(string? status, NameSuggestionStore suggestions, CancellationToken ct) =>
        status is null or "pending" or "accepted" or "rejected"
            ? Results.Ok(new SuggestionList(await suggestions.ListAsync(status ?? "pending", MaxSuggestions, ct)))
            : Invalid("status", "Must be pending, accepted or rejected.");

    /// <summary>
    /// Applies a pending suggestion: a voice is named as <c>POST /people</c> does, a label names its segments, a voice group is
    /// named as its card is. 200 with the person; 404 for an unknown suggestion; 409 when it is no longer pending.
    /// </summary>
    private static async Task<IResult> AcceptSuggestionAsync(
        Guid id, NameSuggestionStore suggestions, PeopleStore people, TimeProvider time, CancellationToken ct)
    {
        var (result, personId) = await suggestions.AcceptAsync(id, time.GetUtcNow(), ct);
        return result switch
        {
            SuggestionDecision.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such suggestion."),
            SuggestionDecision.NotPending => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The suggestion is no longer pending."),
            _ => Results.Ok(await people.GetAsync(personId!.Value, ct)),
        };
    }

    /// <summary>Rejects a pending suggestion; its name is never suggested again for that voice. 204; 404 or 409 as accept.</summary>
    private static async Task<IResult> RejectSuggestionAsync(Guid id, NameSuggestionStore suggestions, TimeProvider time, CancellationToken ct) =>
        await suggestions.RejectAsync(id, time.GetUtcNow(), ct) switch
        {
            SuggestionDecision.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such suggestion."),
            SuggestionDecision.NotPending => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The suggestion is no longer pending."),
            _ => Results.NoContent(),
        };

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

    /// <summary>The person page: last seen, the newest conversations and facts, open tasks owed to them, the note. 404 for an unknown person.</summary>
    private static async Task<IResult> ViewAsync(Guid id, PeopleStore people, CancellationToken ct) =>
        await people.ViewAsync(id, ct) is { } view ? Results.Ok(view) : NotFound();

    public sealed record PersonList(IReadOnlyList<PersonRow> Items);

    private static async Task<IResult> ListAsync(string? tag, PeopleStore people, CancellationToken ct) =>
        tag is not null && TagName.Normalize(tag) is null
            ? TagEndpoints.Invalid("tag")
            : Results.Ok(new PersonList(await people.ListAsync(TagName.Normalize(tag), ct)));

    /// <summary>Adds the tag, created when new. 200 <c>{ tags }</c>, also when the person had it; 400 for a bad name, 404, 409 at 20 tags.</summary>
    private static async Task<IResult> AddTagAsync(Guid id, string name, TagStore tags, TimeProvider time, CancellationToken ct)
    {
        if (TagName.Normalize(name) is not { } normalized)
        {
            return TagEndpoints.InvalidName();
        }

        return await tags.AddToPersonAsync(id, normalized, time.GetUtcNow(), ct) switch
        {
            TagAdd.NoItem => NotFound(),
            TagAdd.TooMany => TagEndpoints.TooMany(),
            _ => Results.Ok(new TagEndpoints.ItemTags(await tags.OfPersonAsync(id, ct))),
        };
    }

    /// <summary>Removes the tag. 200 <c>{ tags }</c>, also when the person did not have it; 400 for a bad name, 404.</summary>
    private static async Task<IResult> RemoveTagAsync(Guid id, string name, TagStore tags, CancellationToken ct) =>
        TagName.Normalize(name) is not { } normalized
            ? TagEndpoints.InvalidName()
            : await tags.RemoveFromPersonAsync(id, normalized, ct)
                ? Results.Ok(new TagEndpoints.ItemTags(await tags.OfPersonAsync(id, ct)))
                : NotFound();

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

    internal static string? Name(JsonElement body)
    {
        if (!body.TryGetProperty("name", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var name = value.GetString()!.Trim();
        return name.Length is > 0 and <= MaxNameLength ? name : null;
    }

    internal static async Task<JsonElement?> ReadObjectAsync(HttpRequest request, CancellationToken ct)
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

    internal static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult FactNotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such fact.");

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such person.");
}
