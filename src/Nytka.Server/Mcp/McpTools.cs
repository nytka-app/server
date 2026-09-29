using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

public sealed record McpConversationItem(
    Guid Id, DateTime StartedAt, DateTime EndedAt, string? Title, string? Summary, string Preview);

public sealed record McpConversationList(IReadOnlyList<McpConversationItem> Items, DateTime? NextBefore);

public sealed record McpTask(Guid Id, string Text, bool Done);

public sealed record McpConversation(
    Guid Id, DateTime StartedAt, DateTime EndedAt, string? Title, string? Summary, IReadOnlyList<McpTask> Tasks,
    string? Transcript, bool Truncated);

public sealed record McpTaskList(IReadOnlyList<McpTaskItem> Items, Guid? NextBefore);

/// <summary>
/// The read-only tools (docs/specs/v0.2.md, MCP). They read through <see cref="McpQueries"/>, and their fields
/// and rules match the REST API. Later versions add tools in their own classes; the assembly scan finds them.
/// </summary>
[McpServerToolType]
public sealed class McpTools(McpQueries queries)
{
    public const int PreviewLength = 140;
    public const int MaxTranscriptChars = 60_000;

    /// <summary>Like REST, every field is written, null ones too.</summary>
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "list_conversations", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(McpConversationList))]
    [Description("Lists conversations, newest first. Pass nextBefore as before to read the next page.")]
    public async Task<CallToolResult> ListConversationsAsync(
        [Description("Only conversations that started at or after this time (ISO 8601).")] string? since = null,
        [Description("Only conversations that started before this time (ISO 8601).")] string? before = null,
        [Description("How many to return, 1 to 50; the default is 20.")] int limit = 20,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 50);
        var rows = await queries.ListConversationsAsync(ParseTime(since, nameof(since)), ParseTime(before, nameof(before)), take, ct);
        var items = rows.Select(r => new McpConversationItem(r.Id, r.StartedAt, r.EndedAt, r.Title, r.Summary, Trim(r.Preview))).ToList();
        return Ok(new McpConversationList(items, items.Count == take ? items[^1].StartedAt : null));
    }

    [McpServerTool(Name = "get_conversation", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(McpConversation))]
    [Description("Reads one conversation: its title, summary, tasks and transcript.")]
    public async Task<CallToolResult> GetConversationAsync(
        [Description("The conversation's id (UUID).")] string id,
        [Description("Whether to include the transcript; the default is true.")] bool transcript = true,
        CancellationToken ct = default)
    {
        var conversationId = ParseId(id, nameof(id));
        if (await queries.GetConversationAsync(conversationId, ct) is not { } conversation)
        {
            return Error("No such conversation.");
        }

        var tasks = (await queries.ConversationTasksAsync(conversationId, ct)).Select(t => new McpTask(t.Id, t.Text, t.Done)).ToList();
        if (!transcript)
        {
            return Ok(new McpConversation(
                conversation.Id, conversation.StartedAt, conversation.EndedAt, conversation.Title, conversation.Summary, tasks, null, false));
        }

        var lines = TranscriptText.Render((await queries.SegmentsAsync(conversationId, MaxTranscriptChars * 2, ct))
            .Select(s => new TranscriptSegment(new DateTimeOffset(s.StartedAt, TimeSpan.Zero), s.Speaker, s.Text)));
        var windows = TranscriptWindows.Split(lines, MaxTranscriptChars);
        return Ok(new McpConversation(
            conversation.Id, conversation.StartedAt, conversation.EndedAt, conversation.Title, conversation.Summary, tasks,
            windows.Count == 0 ? "" : windows[0], windows.Count > 1));
    }

    [McpServerTool(Name = "list_tasks", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(McpTaskList))]
    [Description("Lists tasks taken from conversations, newest first. Pass nextBefore as before to read the next page.")]
    public async Task<CallToolResult> ListTasksAsync(
        [Description("open (the default) or done.")] string? status = null,
        [Description("Only tasks of this conversation (UUID).")] string? conversationId = null,
        [Description("A task id (UUID): only tasks older than it.")] string? before = null,
        [Description("How many to return, 1 to 200; the default is 50.")] int limit = 50,
        CancellationToken ct = default)
    {
        var done = status switch
        {
            null or "open" => false,
            "done" => true,
            _ => throw new McpProtocolException("status must be open or done.", McpErrorCode.InvalidParams),
        };

        var take = Math.Clamp(limit, 1, 200);
        var items = await queries.ListTasksAsync(
            done,
            conversationId is null ? null : ParseId(conversationId, nameof(conversationId)),
            before is null ? null : ParseId(before, nameof(before)),
            take, ct);
        return Ok(new McpTaskList(items, items.Count == take ? items[^1].Id : null));
    }

    private static CallToolResult Ok<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    // Bad arguments are JSON-RPC -32602 (the SDK would report a value it cannot bind as a tool error).
    private static Guid ParseId(string value, string name) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new McpProtocolException($"{name} must be a UUID.", McpErrorCode.InvalidParams);

    // ISO 8601 with an offset or Z, or a date alone (UTC midnight). "o" and K would also take a time without an
    // offset, which would mean the server's zone, so the offset is spelled out.
    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd",
    ];

    private static DateTimeOffset? ParseTime(string? value, string name) =>
        value is null
            ? null
            : DateTimeOffset.TryParseExact(value, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                // Npgsql takes only UTC offsets for timestamptz.
                ? time.ToUniversalTime()
                : throw new McpProtocolException($"{name} must be an ISO 8601 time with an offset, or a date.", McpErrorCode.InvalidParams);

    /// <summary>The preview as REST cuts it: 140 characters, never inside a surrogate pair.</summary>
    private static string Trim(string preview) =>
        preview.Length > PreviewLength
            ? preview[..(char.IsHighSurrogate(preview[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength)]
            : preview;
}
