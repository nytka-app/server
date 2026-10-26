using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Ai;
using Nytka.Server.Api;
using Nytka.Server.Digests;
using Nytka.Server.Privacy;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

/// <summary>The <c>list_events</c> tool: a day's derived structure, the fields of <c>GET /api/v1/events</c>.</summary>
[McpServerToolType]
public sealed class McpEventTools(DayEventStore events, SettingsService settings, SensitivityScope sensitivity, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "list_events", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(DayEventEndpoints.DayEventsResponse))]
    [Description(
        "Lists a day's derived events: who joined and left a conversation, media and calls, silence of at least events.silenceMinutes, " +
        "and being alone with only the wearer speaking. An event tied to a conversation a hidden sensitivity class covers is left out.")]
    public async Task<CallToolResult> ListEventsAsync(
        [Description("The day, yyyy-MM-dd; today in tz (or user.timeZone) when left out.")] string? date = null,
        [Description("An IANA time zone such as Europe/Kyiv or UTC; user.timeZone when left out.")] string? tz = null,
        CancellationToken ct = default)
    {
        TimeZoneInfo zone;
        if (tz is null)
        {
            zone = UserTimeZone.Resolve(settings);
        }
        else if (tz.Length > 64 || !UserTimeZone.TryFind(tz, out zone!))
        {
            throw new McpProtocolException("tz must be an IANA time zone such as Europe/Kyiv or UTC.", McpErrorCode.InvalidParams);
        }

        DateOnly day;
        if (date is null)
        {
            day = DigestDay.Today(time.GetUtcNow(), zone);
        }
        else if (!DigestDay.TryParse(date, out day))
        {
            throw new McpProtocolException("date must be a date, yyyy-MM-dd.", McpErrorCode.InvalidParams);
        }

        var (from, to) = DigestDay.Bounds(day, zone);
        var rows = await events.ListAsync(from, to, sensitivity.Hidden, ct);
        var element = JsonSerializer.SerializeToElement(
            new DayEventEndpoints.DayEventsResponse(DigestDay.Text(day), [.. rows.Select(DayEventEndpoints.DayEventView.From)]), Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = element.GetRawText() }], StructuredContent = element };
    }
}
