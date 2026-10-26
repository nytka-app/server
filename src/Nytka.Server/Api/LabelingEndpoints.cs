using System.Text.RegularExpressions;
using Npgsql;
using Nytka.Server.Auth;
using Nytka.Server.Labeling;
using Nytka.Server.People;
using Nytka.Server.Settings;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The labelling API (docs/reference/api.md, Labelling), for the labeller apps: tokens of scope <c>admin</c> or <c>labeling</c>, and a
/// <c>labeling</c> token reaches nothing else. A <c>400</c> here is <c>{ field, error }</c>, the shape both clients read.
/// </summary>
public static partial class LabelingEndpoints
{
    public const int DefaultNext = 30;
    public const int MaxNext = 50;
    public const int MaxContextSeconds = 4;
    public const int DefaultLabels = 500;
    public const int MaxLabels = 2000;
    public const int MaxConfirm = 500;
    public const int MaxImport = 5000;
    public const int MaxImportSegments = 50;
    public const int MaxListenedMs = 600_000;

    /// <summary>The items <c>GET /next</c> considers per task and reason before mixing.</summary>
    private const int Considered = 200;

    public static RouteGroupBuilder MapLabeling(this RouteGroupBuilder api)
    {
        var labeling = api.MapGroup("/labeling").AllowLabeling();
        labeling.MapGet("/tasks", TasksAsync);
        labeling.MapGet("/next", NextAsync);
        labeling.MapGet("/{id:long}/clip", ClipAsync);
        labeling.MapPost("/{id:long}", AnswerAsync);
        labeling.MapGet("/labels", LabelsAsync);
        labeling.MapDelete("/labels/{labelId:long}", DeleteAsync);
        labeling.MapGet("/voice/pending", PendingAsync);
        labeling.MapPost("/voice/confirm", ConfirmAsync);
        labeling.MapPost("/import", ImportAsync);
        return api;
    }

    public sealed record LabelingError(string Field, string Error);

    public sealed record TaskItem(string Task, string Ask, int Open, int Labelled, int? Goal, string? GoalLabel, int? Progress);

    public sealed record TaskList(IReadOnlyList<TaskItem> Tasks);

    public sealed record Guess(string Model, string? Value, float? Score);

    public sealed record Prefill(string? Language);

    public sealed record Item(
        long Id, string Task, string Ask, string Reason, Guid ConversationId, DateTime StartedAt, int DurationMs,
        IReadOnlyList<LabelStore.ItemLine> Lines, IReadOnlyList<LabelStore.ItemLine> ContextBefore, IReadOnlyList<LabelStore.ItemLine> ContextAfter,
        Guess? Guess, Prefill Prefill, float[]? Peaks, string? SpeakerKey);

    public sealed record Session(Guid SessionId, DateTime? LeasedUntil, IReadOnlyList<Item> Items);

    public sealed record AnswerBody(
        string? ClientId, string[]? Source, string? DeviceType, string? Content, string? Transcript, string[]? Flags, string? Language,
        string? PersonId, string? Correction, string? Overlap, bool? LinesDiffer, bool? Skipped, int? ListenedMs, string? AnsweredWith);

    public sealed record Answered(long? LabelId, long? Superseded);

    public sealed record Verdict(long SegmentId, bool? VoiceIsUser, float? VoiceSimilarity, string? SpeechGuess, float? SpeechScore);

    public sealed record Label(
        long LabelId, long? ItemId, string? Task, long[] SegmentIds, Guid ConversationId, DateTime StartedAt, int DurationMs, string[]? Source,
        string? DeviceType, string? Content, string? Transcript, string[] Flags, string? Language, Guid? PersonId, string? Correction,
        string? Overlap, string? AnsweredWith, string Provenance, DateTime CreatedAt, IReadOnlyList<Verdict> Verdicts);

    public sealed record LabelPage(IReadOnlyList<Label> Items, DateTime? NextSince);

    public sealed record PendingList(IReadOnlyList<PendingVoiceLine> Items);

    public sealed record ConfirmBody(long[]? LabelIds);

    public sealed record ConfirmResult(int Learned);

    public sealed record ImportItem(long[]? SegmentIds, string[]? Source, string? Content, string? Transcript, string[]? Flags);

    public sealed record ImportBody(string? Provenance, ImportItem[]? Items);

    public sealed record ImportResult(int Imported, int Skipped);

    /// <summary>Each task with its open items and current labels; <c>device</c> has the detector's ship gate as its goal.</summary>
    private static async Task<IResult> TasksAsync(LabelStore labels, CancellationToken ct) =>
        Results.Ok(new TaskList([.. (await labels.TaskCountsAsync(ct)).Select(t => t.Task == LabelingTasks.Device
            ? new TaskItem(t.Task, LabelingTasks.Ask(t.Task), t.Open, t.Labelled, 100, "not-device lines", t.Progress)
            : new TaskItem(t.Task, LabelingTasks.Ask(t.Task), t.Open, t.Labelled, null, null, null))]));

    /// <summary>
    /// Leases up to <c>limit</c> items of <c>task</c> (or <c>mixed</c>) for 30 minutes. While a lease of the same task is open it returns
    /// that lease's items not yet answered.
    /// </summary>
    private static async Task<IResult> NextAsync(string? task, int? limit, LabelStore labels, TimeProvider time, CancellationToken ct)
    {
        if (task is null || (task != LabelingTasks.Mixed && !LabelingTasks.All.Contains(task)))
        {
            return Bad("task", "unknown task");
        }

        var now = time.GetUtcNow();
        var context = TimeSpan.FromSeconds(MaxContextSeconds);
        if (await labels.OpenLeaseAsync(task, now, ct) is { } lease)
        {
            return Results.Ok(new Session(lease.Session, lease.Until, Items(await labels.ItemsAsync(lease.Ids, context, ct))));
        }

        var take = Math.Clamp(limit ?? DefaultNext, 1, MaxNext);
        var mixed = task == LabelingTasks.Mixed;
        var open = await labels.OpenAsync(mixed ? LabelingTasks.All : [task], Considered, now, ct);
        var chosen = LabelingMix.Choose(open, mixed, take, Random.Shared);
        var session = Guid.NewGuid();
        var until = now + LabelStore.Lease;
        var taken = await labels.TakeAsync([.. chosen.Select(c => c.Id)], task, session, until, now, ct);
        return Results.Ok(new Session(session, until.UtcDateTime, Items(await labels.ItemsAsync(taken, context, ct))));
    }

    private static List<Item> Items(IReadOnlyList<LabelStore.ItemView> views) =>
        [.. views.Select(v => new Item(
            v.Id, v.Task, LabelingTasks.Ask(v.Task), v.Reason, v.ConversationId, v.StartedAt, (int)(v.EndedAt - v.StartedAt).TotalMilliseconds,
            v.Lines, v.ContextBefore, v.ContextAfter, v.GuessModel is { } model ? new Guess(model, v.GuessValue, v.GuessScore) : null,
            new Prefill(LabelingValues.Prefill(string.Join(' ', v.Lines.Select(l => l.Text)))), v.Peaks, v.SpeakerKey))];

    /// <summary>
    /// The item's span and <c>context</c> seconds either side (0 to 4) as <c>audio/ogg</c>, at most 20 s, cut by capture time as a review
    /// clip is. Once the audio is gone, the clip kept with its label (<c>labeling.keepClips</c>), without context; else 404.
    /// </summary>
    private static async Task<IResult> ClipAsync(long id, int? context, LabelStore labels, BatchStore batches, HttpResponse response, CancellationToken ct)
    {
        if (await labels.ItemSpanAsync(id, ct) is not { } span)
        {
            return NotFound("No such item.");
        }

        response.Headers.CacheControl = "no-store";
        var pad = TimeSpan.FromSeconds(Math.Clamp(context ?? 0, 0, MaxContextSeconds));
        var (from, until) = (span.StartedAt - pad, span.EndedAt + pad);
        if (until - from > ReviewStore.MaxClip)
        {
            until = from + ReviewStore.MaxClip;
        }

        var bodies = await batches.SpeechAudioBodiesAsync(
            span.ConversationId, new DateTimeOffset(from, TimeSpan.Zero), new DateTimeOffset(until, TimeSpan.Zero), ct);
        var clip = CardClip.Cut(bodies, from, until, (uint)id, (int)ReviewStore.MaxClip.TotalMilliseconds)
            ?? await labels.KeptClipAsync(id, ct);
        return clip is not null
            ? Results.File(clip, AudioEndpoints.OggMediaType, enableRangeProcessing: true)
            : NotFound("The item's audio is gone.");
    }

    /// <summary>
    /// Stores an answer: <c>200 { labelId, superseded }</c>. The same <c>clientId</c> again returns the first answer's label and changes
    /// nothing; <c>skipped</c> returns the item to the queue and stores nothing. A label's source writes through to the lines' marks.
    /// </summary>
    private static async Task<IResult> AnswerAsync(
        long id, HttpRequest http, LabelStore labels, PeopleStore people, BatchStore batches, SettingsService settings, TimeProvider time,
        CancellationToken ct)
    {
        var body = await TokenEndpoints.ReadBodyAsync<AnswerBody>(http, ct);
        if (body is null)
        {
            return Bad("body", "malformed");
        }

        if (!Guid.TryParse(body.ClientId, out var clientId))
        {
            return Bad("clientId", "required uuid");
        }

        if (body.Skipped == true)
        {
            return await labels.SkipAsync(id, ct) ? Results.Ok(new Answered(null, null)) : NotFound("No such item.");
        }

        if (await labels.ItemLineCountAsync(id, ct) is not { } lineCount)
        {
            return NotFound("No such item.");
        }

        if (Validate(body, lineCount) is { } bad)
        {
            return bad;
        }

        Guid? personId = null;
        if (body.PersonId is not null)
        {
            if (!Guid.TryParse(body.PersonId, out var person) || await people.GetAsync(person, ct) is null)
            {
                return Bad("personId", "unknown person");
            }

            personId = person;
        }

        var label = new NewLabel(
            clientId, body.Source is { } source ? [.. source.Distinct()] : null, body.DeviceType, body.Content, body.Transcript,
            body.Flags is { } flags ? [.. flags.Distinct()] : [], body.Language, personId, body.Correction, body.Overlap,
            body.LinesDiffer == true, body.ListenedMs, body.AnsweredWith, LabelStore.App);
        var now = time.GetUtcNow();
        LabelAnswer answer;
        try
        {
            answer = await labels.AnswerAsync(id, label, now, ct);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The same client id raced itself on another item: the label it made stands.
            return Results.Ok(new Answered(await labels.ByClientAsync(clientId, ct), null));
        }

        if (!answer.Found)
        {
            return NotFound("No such item.");
        }

        if (answer.Created)
        {
            await KeepClipAsync(id, answer.LabelId!.Value, labels, batches, settings, now, ct);
        }

        return Results.Ok(new Answered(answer.LabelId, answer.Superseded));
    }

    /// <summary>With <c>labeling.keepClips</c> on, keeps the item's clip without context beside the label, so it outlives the audio.</summary>
    private static async Task KeepClipAsync(
        long itemId, long labelId, LabelStore labels, BatchStore batches, SettingsService settings, DateTimeOffset now, CancellationToken ct)
    {
        if (!LabelingSettings.KeepClips(settings) || await labels.ItemSpanAsync(itemId, ct) is not { } span)
        {
            return;
        }

        var until = span.EndedAt - span.StartedAt > ReviewStore.MaxClip ? span.StartedAt + ReviewStore.MaxClip : span.EndedAt;
        var bodies = await batches.SpeechAudioBodiesAsync(
            span.ConversationId, new DateTimeOffset(span.StartedAt, TimeSpan.Zero), new DateTimeOffset(until, TimeSpan.Zero), ct);
        if (CardClip.Cut(bodies, span.StartedAt, until, (uint)itemId, (int)ReviewStore.MaxClip.TotalMilliseconds) is { } clip)
        {
            await labels.KeepClipAsync(labelId, clip, now, ct);
        }
    }

    private static IResult? Validate(AnswerBody b, int lineCount)
    {
        if (b.Source is null && b.DeviceType is null && b.Content is null && b.Transcript is null && b.Flags is null && b.Language is null
            && b.PersonId is null && b.Correction is null && b.Overlap is null)
        {
            return Bad("body", "no answer");
        }

        if (b.Source is { } source && (source.Length is < 1 or > 2 || source.Distinct().Count() != source.Length))
        {
            return Bad("source", "1 or 2 values");
        }

        return Unknown("source", b.Source, LabelingValues.Source)
            ?? Unknown("flags", b.Flags, LabelingValues.Flags)
            ?? Unknown("deviceType", b.DeviceType, LabelingValues.DeviceType)
            ?? Unknown("content", b.Content, LabelingValues.Content)
            ?? Unknown("transcript", b.Transcript, LabelingValues.Transcript)
            ?? Unknown("language", b.Language, LabelingValues.Language)
            ?? Unknown("overlap", b.Overlap, LabelingValues.Overlap)
            ?? Unknown("answeredWith", b.AnsweredWith, LabelingValues.AnsweredWith)
            ?? (b.Correction is { } correction && (lineCount != 1 || correction.Length > LabelingValues.MaxCorrection)
                ? Bad("correction", lineCount != 1 ? "single line only" : "too long")
                : null)
            ?? (b.ListenedMs is < 0 or > MaxListenedMs ? Bad("listenedMs", "out of range") : null);
    }

    private static IResult? Unknown(string field, string? value, string[] allowed) =>
        value is not null && !allowed.Contains(value) ? Bad(field, "unknown value") : null;

    private static IResult? Unknown(string field, string[]? values, string[] allowed) =>
        values is not null && values.Any(v => !allowed.Contains(v)) ? Bad(field, "unknown value") : null;

    /// <summary>
    /// Current labels created after <c>since</c>, oldest first, without audio: <c>limit</c> 1 to 2000 (default 500), <c>task</c> one task or
    /// <c>import</c>. Each carries the models' own verdicts on its lines (<c>verdicts</c>), never the rule's, which a label's mark decides.
    /// </summary>
    private static async Task<IResult> LabelsAsync(DateTimeOffset? since, string? task, int? limit, LabelStore labels, CancellationToken ct)
    {
        if (task is not null && task != "import" && !LabelingTasks.All.Contains(task))
        {
            return Bad("task", "unknown task");
        }

        var take = Math.Clamp(limit ?? DefaultLabels, 1, MaxLabels);
        var rows = await labels.ListAsync(since?.ToUniversalTime(), task, take, ct);
        var verdicts = (await labels.VerdictsAsync([.. rows.SelectMany(r => r.SegmentIds).Distinct()], ct)).ToDictionary(v => v.SegmentId);
        return Results.Ok(new LabelPage(
            [.. rows.Select(r => new Label(
                r.LabelId, r.ItemId, r.Task, r.SegmentIds, r.ConversationId, r.StartedAt, r.DurationMs, r.Source, r.DeviceType, r.Content,
                r.Transcript, r.Flags, r.Language, r.PersonId, r.Correction, r.Overlap, r.AnsweredWith, r.Provenance, r.CreatedAt,
                [.. r.SegmentIds.Where(verdicts.ContainsKey).Select(s => verdicts[s]).Select(v =>
                    new Verdict(v.SegmentId, v.VoiceIsUser, v.VoiceSimilarity, v.SpeechGuess, v.SpeechScore))]))],
            rows.Count == take ? rows[^1].CreatedAt : null));
    }

    /// <summary>Deletes the label and its kept clip and clears the marks it set: <c>204</c>, <c>404</c> for an unknown one.</summary>
    private static async Task<IResult> DeleteAsync(long labelId, LabelStore labels, CancellationToken ct) =>
        await labels.DeleteAsync(labelId, ct) ? Results.NoContent() : NotFound("No such label.");

    private static async Task<IResult> PendingAsync(LabelStore labels, CancellationToken ct) =>
        Results.Ok(new PendingList(await labels.PendingVoiceAsync(MaxConfirm, ct)));

    /// <summary>Lets confirmed <c>me</c> labels teach the voiceprint: <c>{ learned }</c>, the lines it learned. <c>400</c> for an unknown label.</summary>
    private static async Task<IResult> ConfirmAsync(HttpRequest http, LabelStore labels, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        var body = await TokenEndpoints.ReadBodyAsync<ConfirmBody>(http, ct);
        if (body?.LabelIds is not { Length: > 0 and <= MaxConfirm } ids)
        {
            return Bad("labelIds", $"1 to {MaxConfirm} label ids");
        }

        return await labels.ConfirmVoiceAsync(
            ids, VoiceSettings.Learns(settings), VoiceRules.LearnMinSeconds, VoiceSettings.NewPrintBelow(settings),
            VoiceSettings.MaxPrints(settings), time.GetUtcNow(), ct) is { } learned
            ? Results.Ok(new ConfirmResult(learned))
            : Bad("labelIds", "unknown label");
    }

    /// <summary>
    /// Imports an old label set by segment id with its <c>provenance</c> (<c>import:</c> and a name). Import-only values are allowed; nothing
    /// writes through to marks. <c>{ imported, skipped }</c>.
    /// </summary>
    private static async Task<IResult> ImportAsync(HttpRequest http, LabelStore labels, TimeProvider time, CancellationToken ct)
    {
        var body = await TokenEndpoints.ReadBodyAsync<ImportBody>(http, ct);
        if (body is null)
        {
            return Bad("body", "malformed");
        }

        if (body.Provenance is not { } provenance || !ProvenancePattern().IsMatch(provenance))
        {
            return Bad("provenance", "import:<name> of a-z, 0-9 and -");
        }

        if (body.Items is not { Length: > 0 and <= MaxImport } items)
        {
            return Bad("items", $"1 to {MaxImport} items");
        }

        var imports = new List<ImportLabel>();
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            var field = $"items[{i}]";
            if (item.SegmentIds is not { Length: > 0 and <= MaxImportSegments } segments)
            {
                return Bad($"{field}.segmentIds", $"1 to {MaxImportSegments} segment ids");
            }

            if (item.Source is null && item.Content is null && item.Transcript is null && item.Flags is null)
            {
                return Bad(field, "no answer");
            }

            var bad = (item.Source is { Length: < 1 or > 2 } ? Bad($"{field}.source", "1 or 2 values") : null)
                ?? Unknown($"{field}.source", item.Source, [.. LabelingValues.Source, .. LabelingValues.SourceImport])
                ?? Unknown($"{field}.content", item.Content, [.. LabelingValues.Content, .. LabelingValues.ContentImport])
                ?? Unknown($"{field}.transcript", item.Transcript, [.. LabelingValues.Transcript, .. LabelingValues.TranscriptImport])
                ?? Unknown($"{field}.flags", item.Flags, LabelingValues.Flags);
            if (bad is not null)
            {
                return bad;
            }

            imports.Add(new ImportLabel(segments, item.Source is { } s ? [.. s.Distinct()] : null, item.Content, item.Transcript,
                item.Flags is { } f ? [.. f.Distinct()] : []));
        }

        var (imported, skipped) = await labels.ImportAsync(provenance, imports, time.GetUtcNow(), ct);
        return Results.Ok(new ImportResult(imported, skipped));
    }

    private static IResult Bad(string field, string error) => Results.Json(new LabelingError(field, error), statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotFound(string title) => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title);

    [GeneratedRegex("^import:[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex ProvenancePattern();
}
