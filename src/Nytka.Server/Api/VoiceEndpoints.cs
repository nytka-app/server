using System.Text.Json;
using Microsoft.Net.Http.Headers;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Wav;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The voice endpoints (docs/specs/your-voice.md, API): enroll, inspect, reset and forget the wearer's voice, mark a
/// segment, and list labels for evaluation. All need <c>admin</c>. No answer carries a voiceprint or a fingerprint.
/// </summary>
public static class VoiceEndpoints
{
    public const string WavMediaType = "audio/wav";

    /// <summary>120 s of 16 kHz 16-bit WAV and room for its header; a frames body of 120 s is far smaller.</summary>
    public const int MaxBodyBytes = (VoiceEnrollment.MaxSeconds * Timeline.SampleRate * 2) + (64 * 1024);

    public const int DefaultLimit = 500;
    public const int MaxLimit = 5000;

    private static readonly string[] WavMediaTypes = [WavMediaType, "audio/wave", "audio/x-wav"];

    public static RouteGroupBuilder MapVoice(this RouteGroupBuilder api)
    {
        var voice = api.MapGroup("/voice");
        voice.MapGet("", GetAsync);
        voice.MapDelete("", ForgetAsync);
        voice.MapPost("/enrollment", EnrollAsync);
        voice.MapPost("/reset", ResetAsync);
        voice.MapGet("/segments", SegmentsAsync);
        api.MapPatch("/segments/{id:long}", MarkAsync);
        return api;
    }

    public sealed record VoiceView(
        bool Enrolled, DateTime? EnrolledAt, DateTime? UpdatedAt, int EnrolledSamples, int LearnedSegments, bool ModelAvailable);

    public sealed record EnrollmentResponse(double SpeechSeconds, int Samples, float MinAgreement);

    public sealed record VoiceSegmentPage(IReadOnlyList<VoiceSegment> Items, DateTime? NextSince);

    private static async Task<IResult> GetAsync(VoiceStore voices, SpeakerModel model, CancellationToken ct) =>
        Results.Ok(await ViewAsync(voices, model, ct));

    private static async Task<VoiceView> ViewAsync(VoiceStore voices, SpeakerModel model, CancellationToken ct) =>
        await voices.GetStatusAsync(ct) is { } status
            ? new VoiceView(
                true, status.EnrolledAt, status.UpdatedAt, status.EnrolledCount, status.CentroidCount - status.EnrolledCount, model.Available)
            : new VoiceView(false, null, null, 0, 0, model.Available);

    /// <summary>
    /// Body: one or more chunks of Opus frames back to back (<c>application/vnd.nytka.frames.v1</c>) or a 16 kHz mono 16-bit
    /// WAV (<c>audio/wav</c>), at most 120 s. The length is checked before anything is decoded.
    /// </summary>
    private static async Task<IResult> EnrollAsync(
        HttpRequest http, string? mode, SpeakerModel model, VoiceEnrollment enrollment, CancellationToken ct)
    {
        if (mode is not (null or "replace" or "add"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["mode"] = ["Must be replace or add, or left out."] });
        }

        if (model.Embedder is not { } embedder || model.Id is not { } modelId)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The speaker model is not installed.");
        }

        var mediaType = MediaTypeHeaderValue.TryParse(http.ContentType, out var parsed) ? parsed.MediaType.Value : null;
        var wav = WavMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
        if (!wav && !string.Equals(mediaType, ChunkFormat.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: $"Send {ChunkFormat.MediaType} or {WavMediaType}.");
        }

        if (http.ContentLength > MaxBodyBytes || await ChunkEndpoints.ReadLimitedAsync(http.Body, MaxBodyBytes, ct) is not { } body)
        {
            return TooLong();
        }

        Timeline timeline;
        try
        {
            if (wav)
            {
                var audio = WavReader.Read(body);
                if (audio.SampleRate != Timeline.SampleRate || audio.Channels != 1)
                {
                    return BadAudio("The WAV must be 16 kHz mono 16-bit.");
                }

                if (audio.Samples.Length > VoiceEnrollment.MaxSeconds * Timeline.SampleRate)
                {
                    return TooLong();
                }

                timeline = Timeline.Continuous(audio.Samples);
            }
            else
            {
                var frames = ChunkFormat.ReadAll(body).SelectMany(c => c.Frames).ToList();
                if (frames.Count > VoiceEnrollment.MaxSeconds * 1000 / Frame.DurationMs)
                {
                    return TooLong();
                }

                timeline = Timeline.Decode(frames);
            }
        }
        catch (Exception error) when (error is ChunkFormatException or InvalidDataException)
        {
            return BadAudio(error.Message);
        }

        var outcome = await enrollment.EnrollAsync(
            embedder, modelId, timeline, mode == "add" ? EnrollmentMode.Add : EnrollmentMode.Replace, ct);
        return outcome.Verdict switch
        {
            EnrollmentVerdict.Enrolled => Results.Ok(new EnrollmentResponse(outcome.SpeechSeconds, outcome.Samples, outcome.MinAgreement!.Value)),
            EnrollmentVerdict.OtherModel => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The voiceprint was made by another speaker model: enroll again with mode=replace."),
            _ => Unprocessable(outcome),
        };
    }

    /// <summary><c>reason</c> lets the app say which rule failed; the title says it in words.</summary>
    private static IResult Unprocessable(EnrollmentOutcome outcome)
    {
        var (reason, title) = outcome.Verdict switch
        {
            EnrollmentVerdict.TooLittleSpeech => (
                "too-little-speech", $"Too little speech: at least {VoiceEnrollment.MinSpeechSeconds:0} seconds are needed."),
            EnrollmentVerdict.TooFewSamples => (
                "too-few-samples", $"Too few samples: the speech must make at least {VoiceEnrollment.MinSamples} windows of {VoiceEnrollment.MinWindowSeconds:0} seconds."),
            _ => ("samples-disagree", "The samples disagree: another voice or heavy noise is in the recording."),
        };
        return Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: title,
            extensions: new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["speechSeconds"] = outcome.SpeechSeconds,
                ["samples"] = outcome.Samples,
                ["minAgreement"] = outcome.MinAgreement,
            });
    }

    private static async Task<IResult> ResetAsync(
        VoiceStore voices, SpeakerModel model, JobQueue queue, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!await voices.ResetAsync(now, ct))
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No voice is enrolled.");
        }

        await queue.EnqueueAsync(JobKinds.RescoreVoice, new { }, JobKinds.RescoreVoice, now, ct);
        return Results.Ok(await ViewAsync(voices, model, ct));
    }

    /// <summary>Forgets the voice; the wearer's own marks stay. Queues nothing.</summary>
    private static async Task<IResult> ForgetAsync(VoiceStore voices, CancellationToken ct)
    {
        await voices.ForgetAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SegmentsAsync(
        DateTimeOffset? since, DateTimeOffset? until, int? limit, VoiceStore voices, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var items = await voices.ListSegmentsAsync(since?.ToUniversalTime(), until?.ToUniversalTime(), take, ct);
        return Results.Ok(new VoiceSegmentPage(items, items.Count == take ? items[^1].StartedAt : null));
    }

    /// <summary>
    /// Body <c>{ isUser?, personId? }</c>, at least one. <c>isUser</c> is true, false, or null to clear the mark;
    /// <c>personId</c> a person, or null to clear the segment's own person. Answers the segment as a conversation shows it.
    /// </summary>
    private static async Task<IResult> MarkAsync(
        long id, HttpRequest http, VoiceStore voices, PeopleStore people, ConversationStore conversations,
        SettingsService settings, CancellationToken ct)
    {
        JsonElement body;
        try
        {
            body = await http.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            body = default;
        }

        var hasUser = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("isUser", out _);
        var hasPerson = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("personId", out _);
        var errors = new Dictionary<string, string[]>();
        var isUser = (bool?)null;
        if (hasUser)
        {
            var value = body.GetProperty("isUser");
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
            {
                isUser = value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean();
            }
            else
            {
                errors["isUser"] = ["Must be true, false or null."];
            }
        }

        var personId = (Guid?)null;
        if (hasPerson)
        {
            var value = body.GetProperty("personId");
            if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var parsed))
            {
                personId = parsed;
            }
            else if (value.ValueKind != JsonValueKind.Null)
            {
                errors["personId"] = ["Must be the id of a person, or null."];
            }
        }

        if (!hasUser && !hasPerson)
        {
            errors["isUser"] = ["Give isUser, personId or both."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        if (hasPerson)
        {
            switch (await people.SetSegmentPersonAsync(id, personId, ct))
            {
                case SegmentLink.NoPerson:
                    return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such person.");
                case SegmentLink.NoSegment:
                    return NoSegment();
            }
        }

        if (hasUser && !await voices.MarkAsync(id, isUser, VoiceSettings.Learns(settings), VoiceRules.LearnMinSeconds, ct))
        {
            return NoSegment();
        }

        return await conversations.SegmentAsync(id, ct) is { } segment ? Results.Ok(segment) : NoSegment();
    }

    private static IResult NoSegment() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such segment.");

    private static IResult TooLong() => Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge, title: $"An enrollment may not exceed {VoiceEnrollment.MaxSeconds} seconds.");

    private static IResult BadAudio(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unreadable audio.", detail: detail);
}
