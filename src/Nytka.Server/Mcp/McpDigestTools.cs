using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Digests;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

/// <summary>The <c>list_digests</c> tool (docs/specs/v0.7.md): the fields and limits of <c>GET /api/v1/digests</c>.</summary>
[McpServerToolType]
public sealed class McpDigestTools(DigestStore digests)
{
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "list_digests", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DigestPage))]
    [Description("Lists the daily digests, newest date first: a headline, an overview, highlights, decisions and open questions for one day. Pass nextBefore as before to read the next page.")]
    public async Task<CallToolResult> ListDigestsAsync(
        [Description("Only digests of dates before this one (yyyy-MM-dd).")] string? before = null,
        [Description("How many to return, 1 to 100; the default is 30.")] int limit = 30,
        CancellationToken ct = default)
    {
        if (before is not null && !DigestDay.TryParse(before, out _))
        {
            throw new McpProtocolException("before must be a date, yyyy-MM-dd.", McpErrorCode.InvalidParams);
        }

        var page = await digests.ListAsync(before, Math.Clamp(limit, 1, 100), ct);
        var element = JsonSerializer.SerializeToElement(page, Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = element.GetRawText() }], StructuredContent = element };
    }
}
