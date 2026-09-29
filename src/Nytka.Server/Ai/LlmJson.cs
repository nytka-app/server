using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nytka.Server.Ai;

/// <summary>
/// Reads a model's answer against the schema its request carried. A strict-mode endpoint is no
/// guarantee, so this reads strictly too.
/// </summary>
public static class LlmJson
{
    private static readonly JsonSerializerOptions Strict = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>
    /// Strips a Markdown code fence around <paramref name="text"/>, then reads it as
    /// <typeparamref name="T"/>. Property names are camelCase and exact. A missing property, an
    /// extra one, a wrong type or a null where <typeparamref name="T"/> allows none (array elements
    /// included) throws <see cref="LlmException"/>. A property is required when it is a
    /// constructor parameter without a default value, so <typeparamref name="T"/> is a record.
    /// The exception never quotes <paramref name="text"/>: property names in it come from the model.
    /// </summary>
    public static T Parse<T>(string text)
        where T : class
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(StripFence(text));
        }
        catch (JsonException)
        {
            throw new LlmException("The language model endpoint answered with invalid JSON.");
        }

        using (document)
        {
            try
            {
                if (!HasNullElement(document.RootElement) && document.RootElement.Deserialize<T>(Strict) is { } answer)
                {
                    return answer;
                }
            }
            catch (JsonException)
            {
                // Falls through: the message says nothing about where the answer went wrong.
            }
        }

        throw new LlmException("The language model endpoint answered with JSON that does not match the schema.");
    }

    /// <summary>The text without a fence around it: <c>```json</c> or <c>```</c> on the first line, <c>```</c> on the last.</summary>
    private static string StripFence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 6
            || !trimmed.StartsWith("```", StringComparison.Ordinal)
            || !trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var body = trimmed[3..^3];
        var lineEnd = body.IndexOf('\n');
        return lineEnd >= 0 && IsInfoString(body.AsSpan(0, lineEnd)) ? body[(lineEnd + 1)..] : body;
    }

    /// <summary>What may follow the opening fence: a language tag such as <c>json</c>, or nothing.</summary>
    private static bool IsInfoString(ReadOnlySpan<char> line)
    {
        foreach (var c in line.Trim())
        {
            if (!char.IsLetterOrDigit(c) && c is not ('-' or '_' or '+' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The serializer does not check array elements for null, and neither schema in the specs lets
    /// an array hold a null.
    /// </summary>
    private static bool HasNullElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Null || HasNullElement(item))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (HasNullElement(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }
}
