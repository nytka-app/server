using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The digest endpoints (docs/specs/v0.7.md, Daily digest): reading is open to <c>read</c> tokens, a run needs <c>admin</c>.</summary>
public static class DigestEndpoints
{
    public const int DefaultLimit = 30;
    public const int MaxLimit = 100;

    public static RouteGroupBuilder MapDigests(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/digests");
        group.MapGet("", ListAsync).AllowRead();
        group.MapGet("/{id:guid}", GetAsync).AllowRead();
        group.MapPost("/run", RunAsync);
        return api;
    }

    public sealed record RunResponse(string LocalDate);

    private static async Task<IResult> ListAsync(string? before, int? limit, DigestStore digests, CancellationToken ct)
    {
        if (before is not null && !DigestDay.TryParse(before, out _))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["before"] = ["Must be a date, yyyy-MM-dd."] });
        }

        return Results.Ok(await digests.ListAsync(before, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct));
    }

    private static async Task<IResult> GetAsync(Guid id, DigestStore digests, CancellationToken ct) =>
        await digests.GetAsync(id, ct) is { } digest
            ? Results.Ok(digest)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such digest.");

    /// <summary>Queues a run that replaces the digest of a local date, today or earlier.</summary>
    private static async Task<IResult> RunAsync(
        string? date, JobQueue queue, ILlmClient llm, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!DigestDay.TryParse(date, out var day) || day > DigestDay.Today(now, UserTimeZone.Resolve(settings)))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["date"] = ["Must be a date, yyyy-MM-dd, today or earlier."] });
        }

        if (!llm.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The language model is not configured.");
        }

        var text = DigestDay.Text(day);
        await queue.EnqueueAsync(JobKinds.MakeDigest, new DigestPayload(text, true), JobKinds.MakeDigestKey(text, replace: true), now, ct);
        return Results.Accepted(value: new RunResponse(text));
    }
}
