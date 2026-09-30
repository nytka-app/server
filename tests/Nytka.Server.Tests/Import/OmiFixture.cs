namespace Nytka.Server.Tests.Import;

/// <summary>A small invented Omi export: the shape of <c>GET /v1/users/export</c>, no real speech.</summary>
public static class OmiFixture
{
    public const string HikeMemory = "Likes to hike in the mountains.";

    public const string Cabin = "Book the cabin";

    /// <summary>Over 300 characters of short words, so a cut lands on a word boundary.</summary>
    public static readonly string LongMemory = string.Join(' ', Enumerable.Range(1, 120).Select(i => $"w{i}"));

    /// <summary>
    /// Four conversations: c-1 (kept, three spoken segments and one blank), c-2 (discarded), c-3 (no text) and c-4
    /// (kept, +02:00, finished before its last segment ends). Three memories are dropped or collapsed, four
    /// listed tasks: one repeats a conversation's own, two have no imported conversation.
    /// </summary>
    public static string Json => $$"""
        {
          "conversations": [
            {
              "id": "c-1",
              "started_at": "2026-03-01T09:00:00.000000+00:00",
              "finished_at": "2026-03-01T09:05:00.000000+00:00",
              "discarded": false,
              "structured": {
                "title": "Weekend trip",
                "overview": "Planning a cabin weekend by the lake.",
                "action_items": [
                  { "description": "{{Cabin}}", "completed": false, "created_at": "2026-03-01T09:06:00+00:00" },
                  { "description": "Buy firewood", "completed": true, "created_at": "2026-03-01T09:07:00+00:00" }
                ]
              },
              "transcript_segments": [
                { "text": "Shall we rent the cabin by the lake?", "speaker": "SPEAKER_0", "speaker_id": 0, "is_user": true, "start": 0.0, "end": 2.5 },
                { "text": "Yes, and bring the canoe.", "speaker": "SPEAKER_1", "speaker_id": 1, "is_user": false, "start": 3.0, "end": 4.25 },
                { "text": "   ", "speaker": "SPEAKER_1", "speaker_id": 1, "is_user": false, "start": 4.5, "end": 5.0 },
                { "text": "Then it is settled.", "speaker": "SPEAKER_0", "speaker_id": 0, "is_user": true, "start": 6.0, "end": 7.0 }
              ]
            },
            {
              "id": "c-2",
              "started_at": "2026-03-01T12:00:00+00:00",
              "finished_at": "2026-03-01T12:01:00+00:00",
              "discarded": true,
              "structured": { "title": "Thrown away", "overview": "", "action_items": [] },
              "transcript_segments": [
                { "text": "Noise noise noise.", "speaker": "SPEAKER_0", "speaker_id": 0, "is_user": true, "start": 0, "end": 1 }
              ]
            },
            {
              "id": "c-3",
              "started_at": "2026-03-01T13:00:00+00:00",
              "finished_at": "2026-03-01T13:01:00+00:00",
              "discarded": false,
              "structured": { "title": "Silence", "overview": "", "action_items": [] },
              "transcript_segments": [
                { "text": "  ", "speaker": "SPEAKER_0", "speaker_id": 0, "is_user": true, "start": 0, "end": 1 }
              ]
            },
            {
              "id": "c-4",
              "started_at": "2026-03-02T10:00:00+02:00",
              "finished_at": "2026-03-02T10:00:10+02:00",
              "discarded": false,
              "structured": { "title": "Garden", "overview": "Which tomatoes to plant.", "action_items": [] },
              "transcript_segments": [
                { "text": "The tomatoes go by the fence.", "speaker": "SPEAKER_2", "speaker_id": 0, "is_user": false, "start": 5.0, "end": 30.0 }
              ]
            }
          ],
          "memories": [
            { "content": "{{HikeMemory}}", "created_at": "2026-03-01T09:10:00+00:00", "conversation_id": "c-1", "is_dismissed": false },
            { "content": "likes to HIKE in the mountains", "created_at": "2026-03-01T09:11:00+00:00", "conversation_id": "c-1", "is_dismissed": false },
            { "content": "Hates the cold.", "created_at": "2026-03-01T09:12:00+00:00", "conversation_id": "c-1", "is_dismissed": true },
            { "content": "{{LongMemory}}", "created_at": "2026-03-01T09:13:00+00:00", "conversation_id": "c-9", "is_dismissed": false },
            { "content": "Grows tomatoes.", "created_at": "2026-03-02T08:20:00+00:00", "conversation_id": "c-3", "is_dismissed": false }
          ],
          "action_items": [
            { "description": "{{Cabin}}!", "completed": false, "created_at": "2026-03-01T09:06:30+00:00", "conversation_id": "c-1" },
            { "description": "Call the ferry company", "completed": false, "created_at": "2026-03-01T09:08:00+00:00", "conversation_id": "c-1" },
            { "description": "Belongs to a thrown-away talk", "completed": false, "created_at": "2026-03-01T12:02:00+00:00", "conversation_id": "c-2" },
            { "description": "Belongs to nothing", "completed": false, "created_at": "2026-03-01T12:03:00+00:00" }
          ],
          "chat_messages": [ { "text": "ignored" } ],
          "geolocation": { "lat": 0, "lng": 0 }
        }
        """;
}
