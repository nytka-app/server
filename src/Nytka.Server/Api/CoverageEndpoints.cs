using Nytka.Server.Ai;
using Nytka.Server.Coverage;
using Nytka.Server.Digests;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The "Nothing is lost" report (docs/specs/coverage.md): what the app counted against what arrived. Admin only, like the samples it reads.</summary>
public static class CoverageEndpoints
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 5000;
    public const int MaxDays = 62;
    public const int MaxHourDays = 14;

    public static RouteGroupBuilder MapCoverage(this RouteGroupBuilder api)
    {
        api.MapGet("/coverage", GetAsync);
        return api;
    }

    private static async Task<IResult> GetAsync(
        DateTimeOffset? from, DateTimeOffset? to, string? bucket, int? limit,
        DiagnosticsStore diagnostics, ChunkStore chunks, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var zone = UserTimeZone.Resolve(settings);
        var errors = new Dictionary<string, string[]>();

        var size = CoverageBucket.Day;
        if (bucket is not null && !Enum.TryParse(bucket, ignoreCase: true, out size))
        {
            errors["bucket"] = ["Must be day or hour."];
        }

        var end = to is { } given ? given.ToUniversalTime() : now;
        if (end > now)
        {
            end = now;
        }

        // Seven local days ending today, so the first bucket is a whole day.
        var start = from?.ToUniversalTime() ?? DigestDay.Bounds(DigestDay.Today(now, zone).AddDays(-6), zone).From;
        if (start >= end)
        {
            errors["from"] = ["Must be before to, which is now at the latest."];
        }
        else if (end - start > TimeSpan.FromDays(size == CoverageBucket.Day ? MaxDays : MaxHourDays))
        {
            errors["to"] = [$"The range may not exceed {(size == CoverageBucket.Day ? MaxDays : MaxHourDays)} days."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var pad = CoverageReport.StaleAfter * 2;
        var samples = (await diagnostics.ListSamplesAsync(start - pad, end + pad, ct))
            .Select(CoverageSample.Parse)
            .OfType<CoverageSample>()
            .ToList();
        var spans = await chunks.ListSpansAsync(
            start, end, samples.Where(s => s.Session is not null).Select(s => s.Session!.Value).Distinct().ToList(), ct);

        return Results.Ok(CoverageReport.Build(
            samples, spans, start, end, zone, size, now, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit)));
    }
}
