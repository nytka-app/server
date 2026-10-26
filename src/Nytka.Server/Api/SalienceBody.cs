using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The wearer's change of an item's salience: a value is their mark, <see langword="null"/> takes the mark back.</summary>
public sealed record SalienceChange(string? Salience);

/// <summary>Reads the optional <c>salience</c> of a PATCH body for tasks, memories and notes.</summary>
internal static class SalienceBody
{
    /// <summary>The JSON body, or <c>default</c> (not an object) when it is missing, not JSON or not sent as JSON.</summary>
    public static async Task<JsonElement> ReadAsync(HttpRequest http, CancellationToken ct)
    {
        try
        {
            return await http.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return default;
        }
    }

    public const string Bad = "Must be vital, ordinary or noise, or null to take your mark back.";

    /// <summary>
    /// False when the field is there and is none of the three values or null. <paramref name="change"/> is null when the body has
    /// no <c>salience</c> at all.
    /// </summary>
    public static bool TryRead(JsonElement body, out SalienceChange? change)
    {
        change = null;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("salience", out var value))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            change = new SalienceChange(null);
            return true;
        }

        if (value.ValueKind == JsonValueKind.String && Salience.Parse(value.GetString()) is { } parsed)
        {
            change = new SalienceChange(parsed);
            return true;
        }

        return false;
    }
}
