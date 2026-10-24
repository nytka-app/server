using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Api;
using Nytka.Server.Privacy;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

/// <summary>The <c>list_topics</c> and <c>get_topic</c> tools: the fields and limits of <c>GET /api/v1/topics</c> and <c>/topics/{id}</c>.</summary>
[McpServerToolType]
public sealed class McpTopicTools(TopicStore topics, TaskStore tasks, SensitivityScope sensitivity)
{
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "list_topics", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(TopicEndpoints.TopicList))]
    [Description("Lists the topic pages: one running page per recurring tag or named person, with its summary, how many conversations it has, its open commitments and when it last came up, most recent first.")]
    public async Task<CallToolResult> ListTopicsAsync(
        [Description("tag or person; all when left out.")] string? kind = null,
        [Description("Only topics whose name starts with this.")] string? q = null,
        [Description("How many to return, 1 to 200; the default is 50.")] int limit = TopicEndpoints.DefaultLimit,
        CancellationToken ct = default)
    {
        if (kind is not (null or TopicStore.Tag or TopicStore.Person))
        {
            throw new McpProtocolException("kind must be tag or person.", McpErrorCode.InvalidParams);
        }

        var items = await topics.ListAsync(kind, string.IsNullOrWhiteSpace(q) ? null : q.Trim(), sensitivity.Hidden, Math.Clamp(limit, 1, TopicEndpoints.MaxLimit), ct);
        return Result(new TopicEndpoints.TopicList(items));
    }

    [McpServerTool(Name = "get_topic", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(TopicEndpoints.TopicPage))]
    [Description("Returns one topic page: its running summary, its conversations newest first (pass nextBefore as before for older ones) and its open commitments. The summary is built from conversation summaries; being on one topic is no evidence that something said in one conversation is true of another.")]
    public async Task<CallToolResult> GetTopicAsync(
        [Description("The topic's UUID, from list_topics.")] string id,
        [Description("Only conversations that started before this time (ISO 8601 with an offset).")] string? before = null,
        [Description("How many conversations to return, 1 to 100; the default is 30.")] int limit = TopicEndpoints.TimelineLimit,
        CancellationToken ct = default)
    {
        var topicId = Guid.TryParse(id, out var parsed) ? parsed : throw new McpProtocolException("id must be a UUID.", McpErrorCode.InvalidParams);
        DateTimeOffset? beforeTime = null;
        if (before is not null)
        {
            beforeTime = DateTimeOffset.TryParse(before, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                ? time.ToUniversalTime()
                : throw new McpProtocolException("before must be an ISO 8601 time with an offset.", McpErrorCode.InvalidParams);
        }

        var page = await TopicEndpoints.PageAsync(topicId, beforeTime, limit, topics, tasks, sensitivity.Hidden, ct)
            ?? throw new McpProtocolException("No such topic.", McpErrorCode.InvalidParams);
        return Result(page);
    }

    private static CallToolResult Result(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        return new CallToolResult { Content = [new TextContentBlock { Text = element.GetRawText() }], StructuredContent = element };
    }
}
