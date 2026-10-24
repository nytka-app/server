using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;
using Nytka.Server.Api;
using Nytka.Server.Auth;
using Nytka.Server.Events;
using Nytka.Server.Privacy;
using Nytka.Storage;

namespace Nytka.Server.Memories;

/// <summary>
/// The memory tools (docs/specs/v0.4.md, MCP; <c>create_memory</c> is Plane NYTKA-81). <c>list_memories</c> is
/// read-only like v0.2's; <c>create_memory</c> needs an <c>admin</c> token (<see cref="AuthExtensions.Policy"/>),
/// and the MCP host's authorization filters hide it from a <c>read</c> token's <c>tools/list</c> and refuse its
/// <c>tools/call</c> too. Their fields and rules match REST's <c>GET</c> and <c>POST /api/v1/memories</c>.
/// </summary>
[McpServerToolType]
public sealed class MemoryTools(
    MemoryStore memories, NpgsqlDataSource dataSource, IEventPublisher events, TimeProvider time, SensitivityScope sensitivity)
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
        [Description("Whether to include facts that are no longer true (their expiresAt has passed); the default is false.")] bool expired = false,
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
        var element = JsonSerializer.SerializeToElement(await memories.ListAsync(cursor, take, time.GetUtcNow(), ct, expired, sensitivity.Hidden), Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }

    [McpServerTool(Name = "create_memory", UseStructuredContent = true, OutputSchemaType = typeof(MemoryRow))]
    [Description("Adds a lasting fact about the user by hand. Requires an admin token.")]
    [Authorize(Policy = AuthExtensions.Policy)]
    public async Task<CallToolResult> CreateMemoryAsync(
        [Description("The fact's text, 1 to 300 characters.")] string text,
        CancellationToken ct = default)
    {
        var valid = MemoryEndpoints.ValidateText(new MemoryEndpoints.TextRequest(text));
        if (valid.Problem is not null)
        {
            throw new McpProtocolException($"text must be 1 to {MemoryEndpoints.MaxTextLength} characters, with a letter or a digit.", McpErrorCode.InvalidParams);
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await memories.AddAsync(connection, transaction, valid.Text!, valid.Fingerprint!, time.GetUtcNow(), ct) is not { } id)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "A memory already holds this fact." }] };
        }

        await events.PublishAsync(new NytkaEvent(NytkaEvent.MemoryCreated, id), connection, transaction, ct);
        await transaction.CommitAsync(ct);
        var element = JsonSerializer.SerializeToElement(await memories.GetAsync(id, ct), Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }
}
