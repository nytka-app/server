using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Api;
using Nytka.Storage;

namespace Nytka.Server.Search;

public sealed record McpHitList(IReadOnlyList<SearchEndpoints.Hit> Items);

/// <summary>The <c>search</c> MCP tool (docs/specs/v0.4.md, MCP): what <c>GET /api/v1/search</c> returns.</summary>
[McpServerToolType]
public sealed class SearchTools(SearchStore search)
{
    /// <summary>Like REST, every field is written, null ones too.</summary>
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "search", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(McpHitList))]
    [Description("Searches transcripts, titles, summaries, memories, and people by name or fact, in Ukrainian and English. Every word must match, as a prefix. One hit per conversation, memory or person, best first; snippets mark matches with <mark>.")]
    public async Task<CallToolResult> SearchAsync(
        [Description("The words to find.")] string query,
        [Description("conversation, memory, person, or any of them (all three by default).")] string[]? kinds = null,
        [Description("How many hits to return, 1 to 30; the default is 10.")] int limit = 10,
        CancellationToken ct = default)
    {
        if (SearchQuery.Parse(query, kinds, out var error) is not { } parsed)
        {
            throw new McpProtocolException(error!, McpErrorCode.InvalidParams);
        }

        var page = await SearchEndpoints.RunAsync(search, parsed, Math.Clamp(limit, 1, 30), 0, ct);
        var element = JsonSerializer.SerializeToElement(new McpHitList(page.Items), Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }
}
