using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Api;
using Nytka.Server.Insights;
using Nytka.Server.Privacy;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

/// <summary>The <c>get_insights</c> tool: the fields of <c>GET /api/v1/insights</c>, filtered the same way (<see cref="SensitivityScope.Hidden"/>).</summary>
[McpServerToolType]
public sealed class McpInsightTools(InsightStore insights, SettingsService settings, SensitivityScope sensitivity, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "get_insights", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(InsightsResponse))]
    [Description("Returns Nytka's Over time insights: talk time, people heard this week, open commitments and evening share. Each is locked with its unlock progress until insights.trustedAfterDays days have had audio, then shows a numbers-only summary, its 28-day series and a baseline (the median of the last 28 days with audio).")]
    public async Task<CallToolResult> GetInsightsAsync(CancellationToken ct = default)
    {
        var response = await InsightEndpoints.ReadAsync(insights, settings, time, sensitivity.Hidden, ct);
        var element = JsonSerializer.SerializeToElement(response, Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = element.GetRawText() }], StructuredContent = element };
    }
}
