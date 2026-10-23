using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Quality;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The nightly quality metrics (docs/explanation/features.md, Quality metrics), per local day. Admin only.</summary>
public static class QualityEndpoints
{
    public const int MaxDays = 366;

    public const int DefaultDays = 30;

    public static RouteGroupBuilder MapQuality(this RouteGroupBuilder api)
    {
        api.MapGet("/quality", GetAsync);
        return api;
    }

    private static async Task<IResult> GetAsync(
        string? from, string? to, QualityStore quality, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        var (response, errors) = await ReadAsync(from, to, quality, settings, time, ct);
        return response is null ? Results.ValidationProblem(errors) : Results.Ok(response);
    }

    /// <summary>
    /// <paramref name="from"/> and <paramref name="to"/> are local dates, both inclusive, <paramref name="to"/> today at the latest;
    /// by default the last <see cref="DefaultDays"/> days. Only measured days are listed. Null with the errors by parameter.
    /// </summary>
    public static async Task<(QualityResponse? Response, Dictionary<string, string[]> Errors)> ReadAsync(
        string? from, string? to, QualityStore quality, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        var zone = UserTimeZone.Resolve(settings);
        var today = DigestDay.Today(time.GetUtcNow(), zone);
        var errors = new Dictionary<string, string[]>();

        var last = today;
        if (to is not null && !DigestDay.TryParse(to, out last))
        {
            errors["to"] = ["Must be a date, yyyy-MM-dd."];
            last = today;
        }

        if (last > today)
        {
            last = today;
        }

        var first = last.AddDays(1 - DefaultDays);
        if (from is not null && !DigestDay.TryParse(from, out first))
        {
            errors["from"] = ["Must be a date, yyyy-MM-dd."];
        }

        if (errors.Count == 0 && first > last)
        {
            errors["from"] = ["Must be on or before to, which is today at the latest."];
        }
        else if (errors.Count == 0 && last.DayNumber - first.DayNumber + 1 > MaxDays)
        {
            errors["to"] = [$"The range may not exceed {MaxDays} days."];
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var rows = await quality.ListAsync(DigestDay.Text(first), DigestDay.Text(last), ct);
        return (new QualityResponse(UserTimeZone.Name(zone), DigestDay.Text(first), DigestDay.Text(last), rows.Select(QualityReport.Day).ToList()), errors);
    }
}
