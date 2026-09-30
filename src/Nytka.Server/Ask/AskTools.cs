using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Ai;

namespace Nytka.Server.Ask;

/// <summary>The <c>ask</c> MCP tool (docs/specs/v0.8.md): what <c>POST /api/v1/ask</c> returns.</summary>
[McpServerToolType]
public sealed class AskTools(AskService ask)
{
    /// <summary>Like REST, every field is written, null ones too.</summary>
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "ask", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(AskResult))]
    [Description("Answers a question from the user's conversations and memories with the configured language model. The answer cites numbered sources as [n]; sources lists only the cited ones.")]
    public async Task<CallToolResult> AskAsync(
        [Description("The question, 1 to 500 characters.")] string question,
        CancellationToken ct = default)
    {
        if (AskEndpoints.ValidQuestion(question) is not { } valid)
        {
            throw new McpProtocolException(
                $"question must be 1 to {AskService.MaxQuestionLength} characters.", McpErrorCode.InvalidParams);
        }

        if (!ask.IsConfigured)
        {
            return Error(AskEndpoints.NotConfigured);
        }

        try
        {
            var element = JsonSerializer.SerializeToElement(await ask.AskAsync(valid, ct), Json);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = element.GetRawText() }],
                StructuredContent = element,
            };
        }
        catch (LlmException error)
        {
            return Error(error.TimedOut ? AskEndpoints.TimedOut : AskEndpoints.Failed);
        }
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
