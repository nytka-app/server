using System.Text;
using Nytka.Server.Ai;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Memories;

/// <summary>
/// Checks the memories extraction took from one conversation before memories cited their lines (docs/how-to/backfill-with-claude.md):
/// one call with the memories and the id-numbered transcript asks which lines each one comes from, then the grounding rule of
/// <see cref="ExtractMemoriesHandler.IsGrounded"/> decides. A memory no line of the wearer's supports becomes unconfirmed (hidden,
/// never deleted); one that is supported gets its sources. Memories you added or edited are left alone.
/// </summary>
public sealed class MemoryRecheck(ILlmClient llm, MemoryStore memories, SettingsService settings)
{
    public const string SchemaName = "memory_recheck";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["memories"],
          "properties": { "memories": { "type": "array", "items": { "type": "object",
            "additionalProperties": false, "required": ["id", "segmentIds"],
            "properties": { "id": { "type": "string" }, "segmentIds": { "type": "array", "items": { "type": "integer" } } } } } } }
        """;

    public const string System =
        """
        You check facts about one person, called "you", against the transcript they were taken from. Lines labelled "Wearer" are yours; a line labelled "Unknown" has no known speaker and is never yours; other labels are other people. Each line starts with its time and its segment id ("#48121").
        For each fact, give the ids (without "#") of the lines that state it, or in which you confirm it. Give an empty list when no line supports it, or when only someone else's or an Unknown line says it about themselves. Copy each fact's id exactly.
        """;

    public sealed record Checked(string Id, IReadOnlyList<long> SegmentIds);

    public sealed record Answer(IReadOnlyList<Checked> Memories);

    /// <summary>The count of memories checked and of those left confirmed; null when the conversation is gone or has none to check.</summary>
    public async Task<(int Checked, int Confirmed)?> RunAsync(Guid conversationId, CancellationToken ct)
    {
        var known = await memories.OfConversationAsync(conversationId, ct);
        if (known.Count == 0 || await memories.ReadInputAsync(conversationId, ct) is not { } input)
        {
            return null;
        }

        var zone = UserTimeZone.Resolve(settings);
        var transcript = string.Join('\n', TranscriptText.Render(input.Segments.Where(s => !s.IsMedia).Select(s =>
            new TranscriptSegment(new DateTimeOffset(s.StartedAt, TimeSpan.Zero), s.Speaker, s.Text, s.Id)), zone));
        var user = new StringBuilder("Facts (id: text):\n")
            .AppendJoin('\n', known.Select(k => $"{k.Id}: {k.Text}"))
            .Append("\n\nTranscript:\n").Append(transcript).ToString();
        var answer = LlmJson.Parse<Answer>(await llm.CompleteJsonAsync(new LlmRequest(SchemaName, Schema, System, user), ct));

        var cited = answer.Memories
            .Where(m => Guid.TryParse(m.Id, out _))
            .GroupBy(m => Guid.Parse(m.Id))
            .ToDictionary(g => g.Key, g => ExtractMemoriesHandler.Cited(g.SelectMany(m => m.SegmentIds).ToList(), input.Segments));
        var results = known
            .Select(k => (k.Id, Lines: cited.GetValueOrDefault(k.Id) ?? []))
            .Select(k => (k.Id, k.Lines, Confirmed: ExtractMemoriesHandler.IsGrounded(k.Lines, input.Segments)))
            .ToList();
        await memories.RecordCheckAsync(results, ct);
        return (results.Count, results.Count(r => r.Confirmed));
    }
}
