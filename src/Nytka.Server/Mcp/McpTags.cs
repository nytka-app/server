using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Api;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

/// <summary>The <c>list_tags</c> tool (docs/specs/tags.md, API): what <c>GET /api/v1/tags</c> returns.</summary>
[McpServerToolType]
public sealed class McpTags(TagStore tags)
{
    /// <summary>Like REST, every field is written.</summary>
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "list_tags", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(TagEndpoints.TagList))]
    [Description("Lists the tags in use on conversations and people, most used first, with how many of each carry them. Use a name as the tag filter of list_conversations, list_people and search.")]
    public async Task<CallToolResult> ListTagsAsync(
        [Description("Only tags whose name starts with this.")] string? query = null,
        CancellationToken ct = default)
    {
        var element = JsonSerializer.SerializeToElement(await TagEndpoints.ListForAsync(query, tags, ct), Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }
}
