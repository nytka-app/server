using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Api;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

public sealed record McpNote(
    Guid Id, Guid ConversationId, string? ConversationTitle, DateTime ConversationStartedAt, string Topic, IReadOnlyList<string> Points, DateTime CreatedAt);

public sealed record McpNoteList(IReadOnlyList<McpNote> Items, Guid? NextBefore);

/// <summary>The <c>list_notes</c> tool (docs/specs/task-kinds.md): the fields and limits of <c>GET /api/v1/notes</c>.</summary>
[McpServerToolType]
public sealed class McpNoteTools(NoteStore notes)
{
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "list_notes", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(McpNoteList))]
    [Description("Lists the advice taken from conversations, newest first: one note per conversation and topic, each with its tips as points. Pass nextBefore as before to read the next page.")]
    public async Task<CallToolResult> ListNotesAsync(
        [Description("Only notes on this topic, such as table-tennis.")] string? topic = null,
        [Description("Only notes of this conversation (UUID).")] string? conversationId = null,
        [Description("A note id (UUID): only notes older than it.")] string? before = null,
        [Description("How many to return, 1 to 200; the default is 50.")] int limit = 50,
        CancellationToken ct = default)
    {
        var normalized = TagName.Normalize(topic);
        if (topic is not null && normalized is null)
        {
            throw new McpProtocolException("topic must be a name of 1 to 32 letters, digits, - or _.", McpErrorCode.InvalidParams);
        }

        var take = Math.Clamp(limit, 1, NoteEndpoints.MaxLimit);
        var items = (await notes.ListAsync(
                normalized,
                conversationId is null ? null : ParseId(conversationId, nameof(conversationId)),
                before is null ? null : ParseId(before, nameof(before)),
                take, ct))
            .Select(n => new McpNote(n.Id, n.ConversationId, n.ConversationTitle, n.ConversationStartedAt, n.Topic, n.Points, n.CreatedAt))
            .ToList();
        var element = JsonSerializer.SerializeToElement(new McpNoteList(items, items.Count == take ? items[^1].Id : null), Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = element.GetRawText() }], StructuredContent = element };
    }

    private static Guid ParseId(string value, string name) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new McpProtocolException($"{name} must be a UUID.", McpErrorCode.InvalidParams);
}
