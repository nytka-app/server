using System.Text.Json;
using Npgsql;
using Nytka.Server.Auth;
using Nytka.Server.Events;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The task endpoints (docs/specs/v0.2.md, track B): list, edit, complete and delete.</summary>
public static class TaskEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxTextLength = 200;

    public static RouteGroupBuilder MapTasks(this RouteGroupBuilder api)
    {
        var tasks = api.MapGroup("/tasks");
        tasks.MapGet("", ListAsync).AllowRead();
        tasks.MapPatch("/{id:guid}", PatchAsync);
        tasks.MapDelete("/{id:guid}", DeleteAsync);
        return api;
    }

    public sealed record TaskPage(IReadOnlyList<TaskRow> Items, Guid? NextBefore);

    private static async Task<IResult> ListAsync(
        string? status, Guid? conversationId, Guid? before, int? limit, TaskStore tasks, CancellationToken ct)
    {
        if (status is not (null or "open" or "done"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Must be open or done."] });
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var items = await tasks.ListAsync(status == "done", conversationId, before, take, ct);
        return Results.Ok(new TaskPage(items, items.Count == take ? items[^1].Id : null));
    }

    /// <summary>Body <c>{ text?, done? }</c>. Read by hand, so a field of the wrong type is a 400 in every environment.</summary>
    private static async Task<IResult> PatchAsync(
        Guid id, JsonElement body, NpgsqlDataSource dataSource, TaskStore tasks, IEventPublisher events, TimeProvider time,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        string? text = null;
        bool? done = null;
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors["body"] = ["Send an object with text or done."];
        }
        else
        {
            if (body.TryGetProperty("text", out var textValue) && textValue.ValueKind != JsonValueKind.Null)
            {
                text = textValue.ValueKind == JsonValueKind.String ? textValue.GetString()!.Trim() : null;
                if (text is null || text.Length is 0 or > MaxTextLength)
                {
                    errors["text"] = [$"The text must be 1 to {MaxTextLength} characters."];
                }
            }

            if (body.TryGetProperty("done", out var doneValue) && doneValue.ValueKind != JsonValueKind.Null)
            {
                if (doneValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    done = doneValue.GetBoolean();
                }
                else
                {
                    errors["done"] = ["Must be true or false."];
                }
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await tasks.UpdateAsync(connection, transaction, id, text, done, time.GetUtcNow(), ct) is not { } update)
        {
            return NotFound();
        }

        if (update.Completed)
        {
            await events.PublishAsync(new NytkaEvent(NytkaEvent.TaskCompleted, id), connection, transaction, ct);
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(update.Task);
    }

    private static async Task<IResult> DeleteAsync(Guid id, TaskStore tasks, TimeProvider time, CancellationToken ct) =>
        await tasks.DeleteAsync(id, time.GetUtcNow(), ct) ? Results.NoContent() : NotFound();

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such task.");
}
