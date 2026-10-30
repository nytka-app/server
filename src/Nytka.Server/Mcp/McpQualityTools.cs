using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Api;
using Nytka.Server.Quality;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

/// <summary>
/// The <c>get_quality</c> tool: the fields and limits of <c>GET /api/v1/quality</c>. Any token may call it, unlike that route: it holds
/// counts and rates only, never text.
/// </summary>
[McpServerToolType]
public sealed class McpQualityTools(QualityStore quality, SettingsService settings, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "get_quality", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(QualityResponse))]
    [Description("Returns Nytka's nightly quality metrics per local day, oldest first: conversations, fragment, untitled and empty-transcript rates, wearer, voice-scored, unknown-speaker and raw-label line shares, media and tagged shares, extracted items by salience, unconfirmed memories and voice groups, attribution (class shares and speech seconds, labels per conversation, unmapped minutes), short-conversation share and conversations per day, each also as a 7-day value. Only measured days are listed.")]
    public async Task<CallToolResult> GetQualityAsync(
        [Description("The first local date (yyyy-MM-dd); the default is 29 days before to.")] string? from = null,
        [Description("The last local date (yyyy-MM-dd), today at the latest; the default is today. At most 366 days in all.")] string? to = null,
        CancellationToken ct = default)
    {
        var (response, errors) = await QualityEndpoints.ReadAsync(from, to, quality, settings, time, ct);
        if (response is null)
        {
            var (name, messages) = errors.First();
            throw new McpProtocolException($"{name}: {messages[0]}", McpErrorCode.InvalidParams);
        }

        var element = JsonSerializer.SerializeToElement(response, Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = element.GetRawText() }], StructuredContent = element };
    }
}
