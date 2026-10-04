using System.Text.Json;
using Nytka.Server.People;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The admin routes of voice grouping (docs/specs/people.md, Layer 2): the "Who is this?" cards and their clips, evaluating
/// it and forgetting what it holds. No answer carries a vector: only ids, durations, similarities, lines and audio.
/// </summary>
public static class VoiceGroupEndpoints
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 5000;

    public static RouteGroupBuilder MapVoiceGroups(this RouteGroupBuilder api)
    {
        var people = api.MapGroup("/people");
        people.MapGet("/voice-eval", EvalAsync);
        people.MapDelete("/voiceprints", ForgetAsync);
        people.MapGet("/cards", CardsAsync);
        people.MapGet("/cards/{kind}/{id:guid}/clip", ClipAsync);
        people.MapPost("/cards/{kind}/{id:guid}", AnswerAsync);
        return api;
    }

    /// <summary>How long a skipped card stays hidden.</summary>
    public static readonly TimeSpan SkipFor = TimeSpan.FromDays(7);

    public sealed record CardClipWindow(DateTime From, DateTime Until);

    public sealed record CardItem(
        string Kind, Guid Id, Guid ConversationId, string? ConversationTitle, Guid? PersonId, string? PersonName, float? Similarity,
        CardClipWindow Clip, IReadOnlyList<CardLine> Lines);

    public sealed record CardList(IReadOnlyList<CardItem> Items);

    /// <summary>
    /// At most 4 cards, at most 2 from one conversation, newest first: a group with no person ("Who is this?") or a pending
    /// match ("Is this Olena?"), each with a clean stretch of at least 5 s and its first lines. Empty with voice matching off.
    /// </summary>
    private static async Task<IResult> CardsAsync(VoiceGroupStore groups, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        if (!PeopleSettings.VoiceMatching(settings))
        {
            return Results.Ok(new CardList([]));
        }

        var (owners, segments) = await groups.CardInputAsync(time.GetUtcNow(), ct);
        return Results.Ok(new CardList([.. CardPicker.Pick(owners, segments).Select(c => new CardItem(
            c.Owner.Kind, c.Owner.Id, c.ConversationId, c.ConversationTitle, c.Owner.PersonId, c.Owner.PersonName, c.Owner.Similarity,
            new CardClipWindow(c.From, c.Until), c.Lines))]));
    }

    /// <summary>The card's clip as <c>audio/ogg</c>, at most 10 s. 404 when the card is gone or its audio is.</summary>
    private static async Task<IResult> ClipAsync(string kind, Guid id, VoiceGroupStore groups, BatchStore batches, CancellationToken ct)
    {
        if (!IsKind(kind))
        {
            return CardNotFound();
        }

        var (owners, segments) = await groups.CardInputAsync(kind, id, ct);
        if (owners.Count == 0 || CardPicker.For(owners[0], segments) is not { } card)
        {
            return CardNotFound();
        }

        var bodies = await batches.SpeechAudioBodiesAsync(
            card.ConversationId, new DateTimeOffset(card.From, TimeSpan.Zero), new DateTimeOffset(card.Until, TimeSpan.Zero), ct);
        return CardClip.Cut(bodies, card.From, card.Until, id) is { } clip
            ? Results.File(clip, AudioEndpoints.OggMediaType, enableRangeProcessing: true)
            : CardNotFound();
    }

    /// <summary>
    /// Body <c>{ personId }</c> or <c>{ name }</c> (a person of that name, any case, else a new one) names a group; for a
    /// match either must be the person it asks about, and confirms it. 200 with the person. <c>{ skip: true }</c> hides the card for 7
    /// days and <c>{ reject: true }</c> answers "not a person" (a group is deleted) or "not them" (a match is never offered
    /// again); both 204. 404 for an unknown card or person.
    /// </summary>
    private static async Task<IResult> AnswerAsync(
        string kind, Guid id, HttpRequest http, VoiceGroupStore groups, PeopleStore people, TimeProvider time, CancellationToken ct)
    {
        if (!IsKind(kind))
        {
            return CardNotFound();
        }

        if (await PeopleEndpoints.ReadObjectAsync(http, ct) is not { } body || Answer(body) is not { } answer)
        {
            return PeopleEndpoints.Invalid("body", "Give exactly one of personId (a person id), name (1 to 80 characters), skip: true or reject: true.");
        }

        var now = time.GetUtcNow();
        if (answer.Skip || answer.Reject)
        {
            return await (answer.Skip ? groups.SkipAsync(kind, id, now + SkipFor, ct) : groups.RejectCardAsync(kind, id, now, ct))
                ? Results.NoContent()
                : CardNotFound();
        }

        Guid? personId;
        if (kind == VoiceGroupStore.MatchKind)
        {
            if (await groups.MatchPersonAsync(id, ct) is not { } asked)
            {
                return CardNotFound();
            }

            var named = answer.PersonId ?? (await people.ListAsync(ct))
                .FirstOrDefault(p => string.Equals(p.Name, answer.Name, StringComparison.OrdinalIgnoreCase))?.Id;
            if (named != asked)
            {
                return PeopleEndpoints.Invalid(answer.PersonId is null ? "name" : "personId", "Must name the person this card asks about.");
            }

            personId = asked;
            if (await groups.ConfirmMatchAsync(id, now, ct) != VoiceConfirm.Ok)
            {
                return CardNotFound();
            }
        }
        else if (answer.PersonId is { } existing)
        {
            personId = existing;
            switch (await groups.ConfirmGroupAsync(id, existing, now, ct))
            {
                case VoiceConfirm.NoPerson:
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such person.");
                case VoiceConfirm.NotFound:
                    return CardNotFound();
            }
        }
        else
        {
            var (result, created) = await groups.NameGroupAsync(id, answer.Name!, now, ct);
            if (result != VoiceConfirm.Ok)
            {
                return CardNotFound();
            }

            personId = created;
        }

        return Results.Ok(await people.GetAsync(personId!.Value, ct));
    }

    private sealed record CardAnswer(Guid? PersonId, string? Name, bool Skip, bool Reject);

    /// <summary>The body's single answer, or null when it holds none or several, or one that does not parse.</summary>
    private static CardAnswer? Answer(JsonElement body)
    {
        Guid? personId = null;
        if (body.TryGetProperty("personId", out var person))
        {
            if (person.ValueKind != JsonValueKind.String || !Guid.TryParse(person.GetString(), out var parsed))
            {
                return null;
            }

            personId = parsed;
        }

        var hasName = body.TryGetProperty("name", out _);
        var name = PeopleEndpoints.Name(body);
        var skip = body.TryGetProperty("skip", out var skipValue) && skipValue.ValueKind == JsonValueKind.True;
        var reject = body.TryGetProperty("reject", out var rejectValue) && rejectValue.ValueKind == JsonValueKind.True;
        return (hasName && name is null) || new[] { personId is not null, hasName, skip, reject }.Count(x => x) != 1
            ? null
            : new CardAnswer(personId, name, skip, reject);
    }

    private static bool IsKind(string kind) => kind is VoiceGroupStore.GroupKind or VoiceGroupStore.MatchKind;

    private static IResult CardNotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such card.");

    public sealed record EvalItem(
        long SegmentId, Guid ConversationId, int DurationMs, Guid? GroupId, Guid? PersonId, Guid? MatchPersonId, float? Similarity);

    public sealed record EvalPage(IReadOnlyList<EvalItem> Items, DateTime? NextSince);

    /// <summary>For the evaluation: fingerprinted segments of other people with their group, person, match and similarity, oldest first. No text.</summary>
    private static async Task<IResult> EvalAsync(
        DateTimeOffset? since, DateTimeOffset? until, int? limit, VoiceGroupStore groups, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var rows = await groups.EvalAsync(since?.ToUniversalTime(), until?.ToUniversalTime(), take, ct);
        return Results.Ok(new EvalPage(
            rows.Select(r => new EvalItem(r.SegmentId, r.ConversationId, r.DurationMs, r.GroupId, r.PersonId, r.MatchPersonId, r.Similarity)).ToList(),
            rows.Count == take ? rows[^1].StartedAt : null));
    }

    /// <summary>Deletes every group, every person voiceprint and every pending match; confirmed segment links stay.</summary>
    private static async Task<IResult> ForgetAsync(VoiceGroupStore groups, CancellationToken ct)
    {
        await groups.ForgetAsync(ct);
        return Results.NoContent();
    }
}
