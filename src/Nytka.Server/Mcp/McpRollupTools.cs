using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Api;
using Nytka.Server.Digests;
using Nytka.Server.Rollups;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

/// <summary>The <c>list_rollups</c> tool: the fields and limits of <c>GET /api/v1/rollups</c>.</summary>
[McpServerToolType]
public sealed class McpRollupTools(RollupStore rollups)
{
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "list_rollups", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RollupEndpoints.RollupList))]
    [Description("Lists the summaries over time, newest first: weeks, months, years and all time, each with what changed since the period before, or one topic's weeks. A locked period had too little recorded to summarize. Pass nextBefore as before to read older ones.")]
    public async Task<CallToolResult> ListRollupsAsync(
        [Description("week, month, year or all; every period when left out.")] string? period = null,
        [Description("A topic's UUID, from list_topics: that topic's weeks instead.")] string? topicId = null,
        [Description("Only periods that start before this date (yyyy-MM-dd).")] string? before = null,
        [Description("How many to return, 1 to 100; the default is 12.")] int limit = RollupEndpoints.DefaultLimit,
        CancellationToken ct = default)
    {
        if (period is not null && !RollupPeriod.Periods.Contains(period))
        {
            throw new McpProtocolException("period must be week, month, year or all.", McpErrorCode.InvalidParams);
        }

        Guid? topic = null;
        if (topicId is not null)
        {
            topic = Guid.TryParse(topicId, out var parsed) ? parsed : throw new McpProtocolException("topicId must be a UUID.", McpErrorCode.InvalidParams);
        }

        if (before is not null && !DigestDay.TryParse(before, out _))
        {
            throw new McpProtocolException("before must be a date, yyyy-MM-dd.", McpErrorCode.InvalidParams);
        }

        var take = Math.Clamp(limit, 1, RollupEndpoints.MaxLimit);
        var items = await rollups.ListAsync(period, topic, before, take, ct);
        var element = JsonSerializer.SerializeToElement(
            new RollupEndpoints.RollupList([.. items.Select(RollupEndpoints.RollupView.From)], items.Count == take ? items[^1].Start : null), Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = element.GetRawText() }], StructuredContent = element };
    }
}
