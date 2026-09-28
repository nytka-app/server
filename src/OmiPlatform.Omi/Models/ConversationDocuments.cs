using System.Text.Json.Serialization;

namespace OmiPlatform.Omi.Models;

/// <summary>
/// Mirrors <c>DeveloperConversation</c> in Omi's OpenAPI spec
/// (github.com/BasedHardware/omi, <c>docs/api-reference/openapi.json</c>, checked 2026-09-28).
/// Field names and nullability are taken from that spec, not the prose docs at docs.omi.me, which
/// disagree with it in places (e.g. they describe a <c>discarded</c>/<c>status</c> field that does
/// not exist on this schema).
/// </summary>
public sealed record DeveloperConversation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("finished_at")] DateTimeOffset? FinishedAt,
    [property: JsonPropertyName("structured")] DeveloperConversationStructured Structured,
    [property: JsonPropertyName("language")] string? Language = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("folder_id")] string? FolderId = null,
    [property: JsonPropertyName("folder_name")] string? FolderName = null,
    [property: JsonPropertyName("geolocation")] Geolocation? Geolocation = null,
    [property: JsonPropertyName("transcript_segments")] IReadOnlyList<DeveloperTranscriptSegment>? TranscriptSegments = null);

public sealed record DeveloperConversationStructured(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("overview")] string Overview,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("emoji")] string Emoji = "🧠",
    [property: JsonPropertyName("action_items")] IReadOnlyList<DeveloperConversationActionItem>? ActionItems = null,
    [property: JsonPropertyName("events")] IReadOnlyList<DeveloperConversationEvent>? Events = null);

public sealed record DeveloperConversationActionItem(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("completed")] bool Completed = false,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt = null,
    [property: JsonPropertyName("due_at")] DateTimeOffset? DueAt = null,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt = null,
    [property: JsonPropertyName("conversation_id")] string? ConversationId = null);

public sealed record DeveloperConversationEvent(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("start")] DateTimeOffset Start,
    [property: JsonPropertyName("description")] string Description = "",
    [property: JsonPropertyName("duration")] int Duration = 30,
    [property: JsonPropertyName("created")] bool Created = false);

public sealed record DeveloperTranscriptSegment(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("speaker_id")] int? SpeakerId = null,
    [property: JsonPropertyName("speaker_name")] string? SpeakerName = null);

public sealed record Geolocation(
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("longitude")] double Longitude,
    [property: JsonPropertyName("address")] string? Address = null,
    [property: JsonPropertyName("google_place_id")] string? GooglePlaceId = null,
    [property: JsonPropertyName("location_type")] string? LocationType = null);
