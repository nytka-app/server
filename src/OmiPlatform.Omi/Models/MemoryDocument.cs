using System.Text.Json.Serialization;

namespace OmiPlatform.Omi.Models;

/// <summary>
/// Mirrors <c>DeveloperMemory</c> in Omi's OpenAPI spec
/// (github.com/BasedHardware/omi, <c>docs/api-reference/openapi.json</c>, checked 2026-09-28).
/// </summary>
public sealed record DeveloperMemory(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content")] string Content = "",
    [property: JsonPropertyName("category")] string Category = "interesting",
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("visibility")] string? Visibility = "private",
    [property: JsonPropertyName("manually_added")] bool ManuallyAdded = false,
    [property: JsonPropertyName("reviewed")] bool Reviewed = false,
    [property: JsonPropertyName("edited")] bool Edited = false,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt = null);
