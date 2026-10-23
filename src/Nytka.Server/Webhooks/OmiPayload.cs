using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using Nytka.Storage;

namespace Nytka.Server.Webhooks;

/// <summary>
/// The payload of a webhook whose format is <c>omi</c>: Omi's memory-creation payload
/// (https://docs.omi.me/doc/developer/apps/Integrations), body only, so an app written for Omi reads it unchanged.
/// Unlike the <c>nytka</c> format it carries the transcript's text: choosing the format is how the owner sends it.
/// Fields Nytka has nothing for are null (or an empty list); docs/guide/webhooks.md lists them.
/// </summary>
public static class OmiPayload
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    /// <summary>The payload for a conversation, or null when it is gone.</summary>
    public static async Task<string?> BuildAsync(Guid conversationId, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var head = await connection.QuerySingleOrDefaultAsync<Head>(new CommandDefinition(
            """
            select id as Id, created_at as CreatedAt, started_at as StartedAt, ended_at as EndedAt,
                   coalesce(title, ai_title) as Title, ai_summary as Overview
            from conversations where id = @id
            """,
            new { id = conversationId }, transaction, cancellationToken: ct));
        if (head is null)
        {
            return null;
        }

        var items = (await connection.QueryAsync<ActionItem>(new CommandDefinition(
            "select text as Description, done as Completed from tasks where conversation_id = @id and deleted_at is null and kind = 'commitment' order by created_at, id",
            new { id = conversationId }, transaction, cancellationToken: ct))).ToList();
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            $"""
            select s.text as Text, coalesce(s.speaker_id, s.speaker) as SpeakerKey, {SpeakerLabel.Column} as SpeakerName,
                   coalesce({SpeakerLabel.IsUser}, false) as IsUser,
                   cast(extract(epoch from s.started_at - c.started_at) as float8) as Start,
                   cast(extract(epoch from s.ended_at - c.started_at) as float8) as "End"
            from segments s
            join conversations c on c.id = s.conversation_id
            {SpeakerLabel.Joins}
            where s.conversation_id = @id and s.speech_kind is distinct from 'media'
            order by s.started_at, s.id
            """,
            new { id = conversationId }, transaction, cancellationToken: ct))).ToList();

        var numbers = new Dictionary<string, int>();
        var segments = rows.Select(r =>
        {
            var key = r.IsUser ? "wearer" : r.SpeakerKey ?? "unknown";
            if (!numbers.TryGetValue(key, out var number))
            {
                number = numbers[key] = numbers.Count;
            }

            return new Segment(r.Text, $"SPEAKER_{number:00}", number, r.SpeakerName, r.IsUser, Math.Round(Math.Max(r.Start, 0), 3), Math.Round(Math.Max(r.End, 0), 3));
        }).ToList();

        return JsonSerializer.Serialize(
            new Memory(
                head.Id, head.CreatedAt, head.StartedAt, head.EndedAt, segments,
                new Structured(head.Title, head.Overview, null, null, items, []), [], false, null, null),
            Json);
    }

    private sealed record Head(Guid Id, DateTime CreatedAt, DateTime StartedAt, DateTime EndedAt, string? Title, string? Overview);

    private sealed record Row(string Text, string? SpeakerKey, string? SpeakerName, bool IsUser, double Start, double End);

    private sealed record ActionItem(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("completed")] bool Completed);

    private sealed record Segment(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("speaker")] string Speaker,
        [property: JsonPropertyName("speakerId")] int SpeakerId,
        [property: JsonPropertyName("speaker_name")] string? SpeakerName,
        [property: JsonPropertyName("is_user")] bool IsUser,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End);

    private sealed record Structured(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("overview")] string? Overview,
        [property: JsonPropertyName("emoji")] string? Emoji,
        [property: JsonPropertyName("category")] string? Category,
        [property: JsonPropertyName("action_items")] IReadOnlyList<ActionItem> ActionItems,
        [property: JsonPropertyName("events")] IReadOnlyList<object> Events);

    private sealed record Memory(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("created_at")] DateTime CreatedAt,
        [property: JsonPropertyName("started_at")] DateTime StartedAt,
        [property: JsonPropertyName("finished_at")] DateTime FinishedAt,
        [property: JsonPropertyName("transcript_segments")] IReadOnlyList<Segment> TranscriptSegments,
        [property: JsonPropertyName("structured")] Structured Structured,
        [property: JsonPropertyName("apps_response")] IReadOnlyList<object> AppsResponse,
        [property: JsonPropertyName("discarded")] bool Discarded,
        [property: JsonPropertyName("folder_id")] string? FolderId,
        [property: JsonPropertyName("folder_name")] string? FolderName);
}
