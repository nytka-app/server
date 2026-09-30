using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

public sealed record McpBookmark(Guid Id, DateTime At, string? Note, string Source, Guid? ConversationId);

public sealed record McpBookmarkList(IReadOnlyList<McpBookmark> Items, DateTime? NextBefore, Guid? NextBeforeId);

/// <summary>The <c>list_bookmarks</c> tool (docs/specs/v0.8.md): the fields and limits of <c>GET /api/v1/bookmarks</c>.</summary>
[McpServerToolType]
public sealed class McpBookmarkTools(BookmarkStore bookmarks)
{
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private static readonly string[] TimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd",
    ];

    [McpServerTool(Name = "list_bookmarks", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(McpBookmarkList))]
    [Description("Lists the moments the wearer bookmarked, newest first. Pass nextBefore as before to read the next page.")]
    public async Task<CallToolResult> ListBookmarksAsync(
        [Description("Only bookmarks made before this time (ISO 8601 with an offset, or a date).")] string? before = null,
        [Description("With before: the id of the last bookmark of the previous page (nextBeforeId), so bookmarks made at the same time are not skipped.")] string? beforeId = null,
        [Description("How many to return, 1 to 100; the default is 30.")] int limit = 30,
        CancellationToken ct = default)
    {
        DateTimeOffset? beforeTime = null;
        if (before is not null)
        {
            beforeTime = DateTimeOffset.TryParseExact(before, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time.ToUniversalTime()
                : throw new McpProtocolException("before must be an ISO 8601 time with an offset, or a date.", McpErrorCode.InvalidParams);
        }

        Guid? beforeGuid = null;
        if (beforeId is not null)
        {
            beforeGuid = Guid.TryParse(beforeId, out var parsed)
                ? parsed
                : throw new McpProtocolException("beforeId must be a UUID.", McpErrorCode.InvalidParams);
        }

        var take = Math.Clamp(limit, 1, 100);
        var items = (await bookmarks.ListAsync(beforeTime, beforeGuid, take, ct))
            .Select(b => new McpBookmark(b.Id, b.At, b.Note, b.Source, b.ConversationId)).ToList();
        var element = JsonSerializer.SerializeToElement(new McpBookmarkList(items, items.Count == take ? items[^1].At : null, items.Count == take ? items[^1].Id : null), Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = element.GetRawText() }], StructuredContent = element };
    }
}
