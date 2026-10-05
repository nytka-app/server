using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Speech;

/// <summary>Reads the kind of a body, for the routes that mark lines.</summary>
public static class SpeechBody
{
    public const string BadKind = "Must be person, media, call or null.";

    /// <summary>True when <paramref name="value"/> is a kind an owner may mark with or null (clear the mark); <paramref name="kind"/> is then the kind.</summary>
    public static bool TryKind(JsonElement value, out string? kind)
    {
        kind = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return value.ValueKind == JsonValueKind.Null || SpeechKinds.IsMark(kind);
    }
}
