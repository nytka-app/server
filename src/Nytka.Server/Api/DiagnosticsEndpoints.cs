using System.Globalization;
using System.Text.Json;
using Npgsql;
using Nytka.Storage;

namespace Nytka.Server.Api;

public static class DiagnosticsEndpoints
{
    public const int MaxBytes = 256 * 1024;
    public const int MaxSamples = 500;
    public const int DefaultLimit = 500;
    public const int MaxLimit = 5000;

    public static RouteGroupBuilder MapDiagnostics(this RouteGroupBuilder api)
    {
        var diagnostics = api.MapGroup("/diagnostics");
        diagnostics.MapPost("", UploadAsync);
        diagnostics.MapGet("", ListAsync);
        return api;
    }

    public sealed record UploadResponse(int Accepted);

    public sealed record DiagnosticsPage(IReadOnlyList<JsonElement> Items, DateTime? NextSince);

    private static async Task<IResult> UploadAsync(
        HttpRequest request, DiagnosticsStore diagnostics, TimeProvider time, CancellationToken ct)
    {
        if (request.ContentLength > MaxBytes)
        {
            return TooLarge();
        }

        var body = await ChunkEndpoints.ReadLimitedAsync(request.Body, MaxBytes, ct);
        if (body is null)
        {
            return TooLarge();
        }

        List<NewDiagnosticSample> samples;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (Parse(document.RootElement) is not { } parsed)
            {
                return Malformed();
            }

            samples = parsed;
        }
        catch (JsonException)
        {
            return Malformed();
        }

        try
        {
            await diagnostics.StoreAsync(samples, time.GetUtcNow(), ct);
        }
        catch (PostgresException error) when (error.SqlState.StartsWith("22", StringComparison.Ordinal))
        {
            // Valid JSON that jsonb refuses (\u0000, a lone surrogate, a number out of range). A 400 stops
            // the app retrying it, and the exception, whose context quotes the payload, is never logged.
            return Malformed();
        }

        return Results.Ok(new UploadResponse(samples.Count));
    }

    private static async Task<IResult> ListAsync(
        DateTimeOffset? since, int? limit, DiagnosticsStore diagnostics, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var rows = await diagnostics.ListAsync(since?.ToUniversalTime(), take, ct);
        var items = rows.Select(r => JsonSerializer.Deserialize<JsonElement>(r.Payload)).ToList();
        return Results.Ok(new DiagnosticsPage(items, rows.Count > 0 ? rows[^1].At : null));
    }

    /// <summary>The samples of a 1..500 element array whose elements carry a GUID <c>id</c> and an ISO 8601 <c>at</c>; null otherwise.</summary>
    private static List<NewDiagnosticSample>? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var length = root.GetArrayLength();
        if (length is < 1 or > MaxSamples)
        {
            return null;
        }

        var samples = new List<NewDiagnosticSample>(length);
        foreach (var sample in root.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object
                || !sample.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !Guid.TryParse(id.GetString(), out var sampleId)
                || !sample.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sampledAt))
            {
                return null;
            }

            samples.Add(new NewDiagnosticSample(sampleId, sampledAt.ToUniversalTime(), sample.GetRawText()));
        }

        return samples;
    }

    private static IResult TooLarge() => Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge, title: $"A diagnostics upload may not exceed {MaxBytes} bytes.");

    private static IResult Malformed() => Results.Problem(
        statusCode: StatusCodes.Status400BadRequest,
        title: $"Send a JSON array of 1 to {MaxSamples} samples, each with an id and an at.");
}
