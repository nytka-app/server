using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Related;
using Nytka.Server.Search;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

public sealed record McpRelatedList(IReadOnlyList<RelatedLink> Items);

/// <summary>The <c>get_related</c> tool: the "Earlier" links of <c>GET /api/v1/related</c>.</summary>
[McpServerToolType]
public sealed class McpRelatedTools(RelatedStore related, EmbeddingState embeddings)
{
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "get_related", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(McpRelatedList))]
    [Description("Returns the earlier moments linked to a conversation, task, memory or note, best first: the earlier conversation, the line (segmentId, at, quote), a score from 0 to 1 and the reasons (meaning, plus a shared person or tag). A link only says two moments are about the same thing; it is no evidence that anything said in one is true of the other.")]
    public async Task<CallToolResult> GetRelatedAsync(
        [Description("What the id is: conversation, task, memory or note.")] string kind,
        [Description("The UUID of that conversation, task, memory or note.")] string id,
        [Description("How many to return, 1 to 20; the default is 5.")] int limit = RelatedRule.MaxLinks,
        CancellationToken ct = default)
    {
        if (kind is not (RelatedKinds.Conversation or RelatedKinds.Task or RelatedKinds.Memory or RelatedKinds.Note))
        {
            throw new McpProtocolException("kind must be conversation, task, memory or note.", McpErrorCode.InvalidParams);
        }

        var parsed = Guid.TryParse(id, out var guid) ? guid : throw new McpProtocolException("id must be a UUID.", McpErrorCode.InvalidParams);
        var items = await RelatedLink.ForAsync(related, embeddings, kind, parsed, Math.Clamp(limit, 1, 20), ct);
        var element = JsonSerializer.SerializeToElement(new McpRelatedList(items), Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = element.GetRawText() }], StructuredContent = element };
    }
}
