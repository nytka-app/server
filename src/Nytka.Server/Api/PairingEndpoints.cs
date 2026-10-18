using Nytka.Server.Auth;
using Nytka.Storage;
using QRCoder;

namespace Nytka.Server.Api;

/// <summary>
/// The pairing endpoint (Plane NYTKA-71): mints a fresh admin token and hands it back as a
/// <c>nytka://pair</c> payload, for the app to scan instead of typing the server address and the
/// token. Needs <c>admin</c>, like token creation (invariant 8, no <c>AllowRead</c> here).
/// </summary>
public static class PairingEndpoints
{
    public static RouteGroupBuilder MapPairing(this RouteGroupBuilder api)
    {
        api.MapPost("/pair", CreateAsync);
        return api;
    }

    /// <summary>Keeps the payload, with its percent-encoded URL, inside what a QR code at level M can hold.</summary>
    private const int MaxUrlLength = 500;

    public sealed record PairRequest(string? Name, string? Url);

    /// <summary>
    /// A pairing code: the <paramref name="Payload"/> text and the same thing as a QR code PNG
    /// (base64), plus the minted token's fields. <paramref name="Token"/> is shown once, like any
    /// other new token.
    /// </summary>
    public sealed record PairingCode(
        Guid Id, string Name, DateTime CreatedAt, string Token, string Payload, string QrCodePng);

    private static async Task<IResult> CreateAsync(HttpRequest http, TokenStore tokens, TimeProvider time, CancellationToken ct)
    {
        var request = await TokenEndpoints.ReadBodyAsync<PairRequest>(http, ct);
        var name = request?.Name?.Trim();
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(name) || name.Length > TokenEndpoints.MaxNameLength)
        {
            errors["name"] = [$"Must be 1 to {TokenEndpoints.MaxNameLength} characters."];
        }

        var url = request?.Url?.Trim();
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            errors["url"] = [$"Must be an absolute http or https URL of at most {MaxUrlLength} characters."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var created = await TokenEndpoints.CreateNamedTokenAsync(tokens, time, name!, NytkaScopes.Admin, ct);
        if (created is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A token with this name is already in use.");
        }

        var payload = $"nytka://pair?url={Uri.EscapeDataString(url!)}&token={Uri.EscapeDataString(created.Token)}";
        using var qrData = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var qrCodePng = Convert.ToBase64String(new PngByteQRCode(qrData).GetGraphic(10));

        http.HttpContext!.Response.Headers.CacheControl = "no-store";
        return Results.Json(
            new PairingCode(created.Id, created.Name, created.CreatedAt, created.Token, payload, qrCodePng),
            statusCode: StatusCodes.Status201Created);
    }
}
