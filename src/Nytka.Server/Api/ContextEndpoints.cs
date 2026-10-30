using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The context-range endpoints (docs/specs/speech-kind.md, Context from the phone): when the owner's phone played sound
/// through its loudspeaker or was in a call, as a kind, a route and two times. Both routes need <c>admin</c>. A range says
/// when the owner was near sound, so no answer repeats a value the app sent and nothing here logs one.
/// </summary>
public static class ContextEndpoints
{
    public const int MaxItems = 500;
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;

    /// <summary>The longest range; the app cuts a longer one, and the table checks the same.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(12);

    /// <summary>How far ahead of the server's clock a range may start, for a phone whose clock runs fast.</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromHours(24);

    /// <summary>No phone clock reads earlier. Npgsql stores the earliest .NET time as <c>-infinity</c>, which the table's length check refuses.</summary>
    public static readonly DateTimeOffset Earliest = DateTimeOffset.UnixEpoch;

    public static RouteGroupBuilder MapContext(this RouteGroupBuilder api)
    {
        var context = api.MapGroup("/context");
        context.MapPost("/ranges", PostAsync);
        context.MapGet("/ranges", ListAsync);
        return api;
    }

    public sealed record ContextRangePage(IReadOnlyList<ContextRangeRow> Items);

    /// <summary>The reasons a row is refused on its own, as codes: a newer app sends kinds an older server does not know.</summary>
    public const string UnknownKind = "unknown-kind";
    public const string BadRoute = "bad-route";
    public const string BadDetail = "bad-detail";
    public const string BadTimes = "bad-times";
    public const string BadContact = "bad-contact";

    /// <summary>
    /// Body <c>{ items: [{ id, kind, route, startedAt, endedAt, detail?, contact?, contactHash? }] }</c>, 1 to 500 items. An item that is no object or has
    /// no UUID id makes the upload a 400 naming the field and the index (<c>items[3].id</c>), never the value. Any other bad item
    /// (a kind the server does not know, a route or detail its kind does not take, a contact on a kind but <c>call</c>, times out of bounds) is refused on its own and
    /// listed in <c>rejected</c> with a reason code, and the rest are stored. Answers <c>{ accepted, skipped, rejected }</c>;
    /// <c>skipped</c> counts the ranges whose id is already stored, a repeat inside the batch included, and a rejected row counts in neither.
    /// </summary>
    private static async Task<IResult> PostAsync(HttpRequest http, ContextRangeStore ranges, TimeProvider time, CancellationToken ct)
    {
        if (await PeopleEndpoints.ReadObjectAsync(http, ct) is not { } body)
        {
            return PeopleEndpoints.Invalid("body", "Must be a JSON object.");
        }

        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        var items = new List<NewContextRange>();
        var rejected = new List<RejectedRange>();
        try
        {
            if (!body.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() is < 1 or > MaxItems)
            {
                return PeopleEndpoints.Invalid("items", $"Must be a list of 1 to {MaxItems} ranges.");
            }

            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                switch (Read(item, index++, now, errors))
                {
                    case NewContextRange range:
                        items.Add(range);
                        break;
                    case RejectedRange refused:
                        rejected.Add(refused);
                        break;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Valid JSON that no text can hold, such as a lone surrogate escape in a name or a value: reading it throws.
            return PeopleEndpoints.Invalid("body", "Must be valid JSON text.");
        }

        return errors.Count > 0
            ? Results.ValidationProblem(errors)
            : Results.Ok(await ranges.InsertAsync(items, now, ct) with { Rejected = rejected });
    }

    /// <summary>
    /// The range, a <see cref="RejectedRange"/> for a row refused on its own, or null after adding the field that makes the whole
    /// upload bad (no object, no UUID id) to <paramref name="errors"/> as <c>items[index].field</c>.
    /// </summary>
    private static object? Read(JsonElement item, int index, DateTimeOffset now, Dictionary<string, string[]> errors)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors[$"items[{index}]"] = ["Must be an object."];
            return null;
        }

        if (!item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idValue.GetString(), out var id))
        {
            errors[$"items[{index}].id"] = ["Must be a UUID."];
            return null;
        }

        if (ContextRangeStore.KindRules.FirstOrDefault(k => k.Name == Text(item, "kind")) is not { } kind)
        {
            return new RejectedRange(id, UnknownKind);
        }

        if (Text(item, "route") is not { } route || !kind.Routes.Contains(route))
        {
            return new RejectedRange(id, BadRoute);
        }

        var hasDetail = item.TryGetProperty("detail", out var detailValue) && detailValue.ValueKind != JsonValueKind.Null;
        var detail = Text(item, "detail");
        if (kind.Details is null ? hasDetail : detail is null || !kind.Details.Contains(detail))
        {
            return new RejectedRange(id, BadDetail);
        }

        if (!ReadContact(item, kind.Name, out var contact, out var contactHash))
        {
            return new RejectedRange(id, BadContact);
        }

        if (!ReadTime(item, "startedAt", out var startedAt) || !ReadTime(item, "endedAt", out var endedAt)
            || startedAt < Earliest || startedAt > now + MaxAhead || endedAt < startedAt || endedAt - startedAt > MaxLength)
        {
            return new RejectedRange(id, BadTimes);
        }

        return new NewContextRange(id, kind.Name, route, startedAt, endedAt, kind.Details is null ? null : detail, contact, contactHash);
    }

    /// <summary>
    /// A call's <c>contact</c> (a display name of 1 to 80 characters, trimmed) and its optional <c>contactHash</c> (64 hex characters,
    /// the app's SHA-256 of the number, kept in lower case). False when either is wrong, is sent without a contact, or is sent on a kind but call.
    /// </summary>
    private static bool ReadContact(JsonElement item, string kind, out string? contact, out string? hash)
    {
        contact = null;
        hash = null;
        var hasContact = item.TryGetProperty("contact", out var contactValue) && contactValue.ValueKind != JsonValueKind.Null;
        var hasHash = item.TryGetProperty("contactHash", out var hashValue) && hashValue.ValueKind != JsonValueKind.Null;
        if (!hasContact && !hasHash)
        {
            return true;
        }

        contact = Text(item, "contact")?.Trim();
        hash = Text(item, "contactHash")?.ToLowerInvariant();
        return kind == "call" && contact is { Length: > 0 and <= PeopleEndpoints.MaxNameLength } && !contact.Any(char.IsControl)
            && (!hasHash || (hash is { Length: 64 } && hash.All(Uri.IsHexDigit)));
    }

    /// <summary>The string property, or null when it is missing or not a string.</summary>
    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A time with an explicit offset, like a bookmark's: one without would mean the server's zone.</summary>
    private static bool ReadTime(JsonElement item, string name, out DateTimeOffset time)
    {
        time = default;
        return item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && BookmarkEndpoints.HasOffset(value.GetString()!) && value.TryGetDateTimeOffset(out time);
    }

    /// <summary>Ranges that started at or after <c>since</c> and before <c>until</c>, oldest start first.</summary>
    private static async Task<IResult> ListAsync(
        DateTimeOffset? since, DateTimeOffset? until, int? limit, ContextRangeStore ranges, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        // Npgsql takes only UTC offsets for timestamptz.
        return Results.Ok(new ContextRangePage(await ranges.ListAsync(since?.ToUniversalTime(), until?.ToUniversalTime(), take, ct)));
    }
}
