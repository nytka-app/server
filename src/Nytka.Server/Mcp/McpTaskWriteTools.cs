using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Api;
using Nytka.Server.Auth;
using Nytka.Server.Events;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

/// <summary>
/// The task write tools (Plane NYTKA-81): adding and completing a task by hand. Each needs an <c>admin</c>
/// token (<see cref="AuthExtensions.Policy"/>, the same <see cref="NytkaScopeRequirement"/> REST uses); the MCP
/// host's authorization filters (<c>AddAuthorizationFilters</c> in <see cref="McpExtensions.AddNytkaMcp"/>) hide
/// these tools from a <c>read</c> token in <c>tools/list</c> and refuse <c>tools/call</c> too.
/// </summary>
[McpServerToolType]
[Authorize(Policy = AuthExtensions.Policy)]
public sealed class McpTaskWriteTools(NpgsqlDataSource dataSource, TaskStore tasks, IEventPublisher events, TimeProvider time)
{
    /// <summary>Like REST, every field is written, null ones too.</summary>
    private static readonly JsonSerializerOptions Json = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    [McpServerTool(Name = "create_task", UseStructuredContent = true, OutputSchemaType = typeof(McpTaskItem))]
    [Description("Adds a task by hand, to a conversation or on its own. Requires an admin token.")]
    public async Task<CallToolResult> CreateTaskAsync(
        [Description("The task's text, 1 to 200 characters.")] string text,
        [Description("The conversation's id (UUID); a task needs none.")] string? conversationId = null,
        [Description("commitment (the default), idea or waiting_on.")] string? kind = null,
        [Description("The person it is owed to or who asked for it (UUID).")] string? personId = null,
        [Description("The due date or time, ISO 8601 (2026-10-24 or 2026-10-24T15:00:00Z).")] string? due = null,
        CancellationToken ct = default)
    {
        var storedKind = kind ?? TaskKinds.Commitment;
        if (!TaskKinds.IsStored(storedKind))
        {
            throw new McpProtocolException("kind must be commitment, idea or waiting_on.", McpErrorCode.InvalidParams);
        }

        var trimmed = text.Trim();
        if (trimmed.Length is 0 or > TaskEndpoints.MaxTextLength)
        {
            throw new McpProtocolException($"text must be 1 to {TaskEndpoints.MaxTextLength} characters.", McpErrorCode.InvalidParams);
        }

        var fingerprint = TextFingerprint.Of(trimmed);
        if (fingerprint.Length == 0)
        {
            return Error("text must contain a letter or a digit.");
        }

        var cId = conversationId is null ? (Guid?)null : ParseId(conversationId, nameof(conversationId));
        var dueAt = TaskEndpoints.TryParseDue(due, out var parsedDue)
            ? parsedDue
            : throw new McpProtocolException("due must be an ISO 8601 date or time.", McpErrorCode.InvalidParams);
        var pId = personId is null ? (Guid?)null : ParseId(personId, nameof(personId));

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var created = await tasks.CreateAsync(connection, transaction, cId, trimmed, fingerprint, pId, storedKind, dueAt, time.GetUtcNow(), ct);
        if (created.Problem != TaskCreateProblem.None)
        {
            return Error(created.Problem switch
            {
                TaskCreateProblem.NoConversation => "No such conversation.",
                TaskCreateProblem.NoPerson => "No such person.",
                _ => TaskEndpoints.DuplicateTitle,
            });
        }

        if (storedKind == TaskKinds.Commitment)
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.TaskCreated, created.Task!.Id), connection, transaction, ct);
        }

        await transaction.CommitAsync(ct);
        return Ok(ToItem(created.Task!));
    }

    [McpServerTool(Name = "complete_task", UseStructuredContent = true, OutputSchemaType = typeof(McpTaskItem))]
    [Description("Marks a task done. Requires an admin token.")]
    public async Task<CallToolResult> CompleteTaskAsync(
        [Description("The task's id (UUID).")] string id,
        CancellationToken ct = default)
    {
        var taskId = ParseId(id, nameof(id));
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var update = await tasks.UpdateAsync(connection, transaction, taskId, null, true, null, null, time.GetUtcNow(), ct);
        if (update is null)
        {
            return Error("No such task.");
        }

        if (update.Completed)
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.TaskCompleted, taskId), connection, transaction, ct);
        }

        await transaction.CommitAsync(ct);
        return Ok(ToItem(update.Task));
    }

    private static McpTaskItem ToItem(TaskRow task) => new(
        task.Id, task.ConversationId, task.ConversationTitle, task.ConversationStartedAt, task.Text, task.Done,
        task.DoneAt, task.CreatedAt, task.PersonId, task.PersonName, task.Kind, task.Due);

    private static CallToolResult Ok<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static Guid ParseId(string value, string name) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new McpProtocolException($"{name} must be a UUID.", McpErrorCode.InvalidParams);
}
