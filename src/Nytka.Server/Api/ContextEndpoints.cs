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

    private const string TimeMessage = "Must be an ISO 8601 time with an offset.";

    private static readonly string KindMessage = $"Must be {string.Join(" or ", ContextRangeStore.Kinds)}.";

    private static readonly string RouteMessage =
        $"Must be {string.Join(", ", ContextRangeStore.Routes.SkipLast(1))} or {ContextRangeStore.Routes[^1]}.";

    /// <summary>
    /// Body <c>{ items: [{ id, kind, route, startedAt, endedAt }] }</c>, 1 to 500 items. Every item is checked before any is
    /// stored, so a bad one makes the upload a 400 that names the field and the index (<c>items[3].kind</c>), never the
    /// value. Answers <c>{ accepted, skipped }</c>; <c>skipped</c> counts the ids the server already held.
    /// </summary>
    private static async Task<IResult> PostAsync(HttpRequest http, ContextRangeStore ranges, TimeProvider time, CancellationToken ct)
    {
        if (await PeopleEndpoints.ReadObjectAsync(http, ct) is not { } body)
        {
            return PeopleEndpoints.Invalid("body", "Must be a JSON object.");
        }

        if (!body.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() is < 1 or > MaxItems)
        {
            return PeopleEndpoints.Invalid("items", $"Must be a list of 1 to {MaxItems} ranges.");
        }

        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        var items = new List<NewContextRange>(list.GetArrayLength());
        var index = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (Read(item, index++, now, errors) is { } range)
            {
                items.Add(range);
            }
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : Results.Ok(await ranges.InsertAsync(items, now, ct));
    }

    /// <summary>The range, or null after adding each bad field to <paramref name="errors"/> as <c>items[index].field</c>.</summary>
    private static NewContextRange? Read(JsonElement item, int index, DateTimeOffset now, Dictionary<string, string[]> errors)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors[$"items[{index}]"] = ["Must be an object."];
            return null;
        }

        var failed = false;
        void Fail(string field, string message)
        {
            errors[$"items[{index}].{field}"] = [message];
            failed = true;
        }

        Guid id = default;
        if (!item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idValue.GetString(), out id))
        {
            Fail("id", "Must be a UUID.");
        }

        var kind = Choice(item, "kind", ContextRangeStore.Kinds);
        if (kind is null)
        {
            Fail("kind", KindMessage);
        }

        var route = Choice(item, "route", ContextRangeStore.Routes);
        if (route is null)
        {
            Fail("route", RouteMessage);
        }

        var hasStart = ReadTime(item, "startedAt", out var startedAt);
        var hasEnd = ReadTime(item, "endedAt", out var endedAt);
        if (!hasStart)
        {
            Fail("startedAt", TimeMessage);
        }
        else if (startedAt < Earliest)
        {
            Fail("startedAt", $"Must not be before {Earliest.Year}.");
        }
        else if (startedAt > now + MaxAhead)
        {
            Fail("startedAt", $"Must be at most {MaxAhead.TotalHours} hours ahead of the server's clock.");
        }

        if (!hasEnd)
        {
            Fail("endedAt", TimeMessage);
        }
        else if (hasStart && endedAt < startedAt)
        {
            Fail("endedAt", "Must not be before startedAt.");
        }
        else if (hasStart && endedAt - startedAt > MaxLength)
        {
            Fail("endedAt", $"Must be at most {MaxLength.TotalHours} hours after startedAt.");
        }

        return failed ? null : new NewContextRange(id, kind!, route!, startedAt, endedAt);
    }

    /// <summary>The string property when it is one of <paramref name="allowed"/>, spelled exactly so; else null.</summary>
    private static string? Choice(JsonElement item, string name, IReadOnlyList<string> allowed) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text && allowed.Contains(text)
            ? text
            : null;

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
