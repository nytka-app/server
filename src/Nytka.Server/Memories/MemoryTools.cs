using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Api;
using Nytka.Storage;

namespace Nytka.Server.Memories;

/// <summary>
/// The memory tool (docs/specs/v0.4.md, MCP), read-only like v0.2's. The assembly scan of the MCP host
/// finds it; its fields and rules match <c>GET /api/v1/memories</c>.
/// </summary>
[McpServerToolType]
public sealed class MemoryTools(MemoryStore memories)
{
    /// <summary>Like REST, every field is written, null ones too.</summary>
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "list_memories", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(MemoryPage))]
    [Description("Lists lasting facts about the user taken from conversations, newest first. Pass nextBefore as before to read the next page.")]
    public async Task<CallToolResult> ListMemoriesAsync(
        [Description("A memory id (UUID): only memories older than it.")] string? before = null,
        [Description("How many to return, 1 to 200; the default is 50.")] int limit = MemoryEndpoints.DefaultLimit,
        CancellationToken ct = default)
    {
        Guid? cursor = null;
        if (before is not null)
        {
            cursor = Guid.TryParse(before, out var id)
                ? id
                : throw new McpProtocolException("before must be a UUID.", McpErrorCode.InvalidParams);
        }

        var take = Math.Clamp(limit, 1, MemoryEndpoints.MaxLimit);
        var element = JsonSerializer.SerializeToElement(await memories.ListAsync(cursor, take, ct), Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }
}
