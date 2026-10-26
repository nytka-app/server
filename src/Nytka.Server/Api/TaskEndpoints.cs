using System.Globalization;
using System.Text.Json;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Events;
using Nytka.Server.Privacy;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The task endpoints (docs/specs/v0.2.md, track B): list, create, edit, complete and delete.</summary>
public static class TaskEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxTextLength = 200;

    /// <summary>Why a task was not added: its conversation has one with this text, or an open task made by hand has.</summary>
    public const string DuplicateTitle = "A task with this text already exists.";

    public static RouteGroupBuilder MapTasks(this RouteGroupBuilder api)
    {
        var tasks = api.MapGroup("/tasks");
        tasks.MapGet("", ListAsync).AllowRead();
        tasks.MapPost("", CreateAsync);
        tasks.MapPatch("/{id:guid}", PatchAsync);
        tasks.MapDelete("/{id:guid}", DeleteAsync);
        return api;
    }

    /// <summary>The <c>kind</c> value that lists every kind.</summary>
    public const string AllKinds = "all";

    public sealed record TaskPage(IReadOnlyList<TaskRow> Items, Guid? NextBefore);

    /// <summary><c>kind</c> is <c>commitment</c> (the default), <c>idea</c>, <c>waiting_on</c> or <c>all</c>.</summary>
    private static async Task<IResult> ListAsync(
        string? status, Guid? conversationId, Guid? before, string? kind, int? limit, TaskStore tasks, SensitivityScope sensitivity,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (status is not (null or "open" or "done"))
        {
            errors["status"] = ["Must be open or done."];
        }

        if (kind is not (null or TaskKinds.Commitment or TaskKinds.Idea or TaskKinds.WaitingOn or AllKinds))
        {
            errors["kind"] = ["Must be commitment, idea, waiting_on or all."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var items = await tasks.ListAsync(status == "done", conversationId, before, StoredKind(kind), take, ct, sensitivity.Hidden);
        return Results.Ok(new TaskPage(items, items.Count == take ? items[^1].Id : null));
    }

    /// <summary>The kind to filter by: the commitment when none is given, null (every kind) for <c>all</c>.</summary>
    public static string? StoredKind(string? kind) => kind switch
    {
        null => TaskKinds.Commitment,
        AllKinds => null,
        _ => kind,
    };

    /// <summary>
    /// A due date or time as ISO 8601 text; a bare date means that day at 00:00 UTC. Null or empty is no due date.
    /// False when the text is not a date.
    /// </summary>
    public static bool TryParseDue(string? text, out DateTime? due)
    {
        due = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var value = text.Trim();
        if (value.Length < 10 || value[4] != '-' || value[7] != '-'
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        due = parsed.UtcDateTime;
        return true;
    }

    /// <summary>
    /// Body <c>{ text, due?, conversationId?, personId? }</c>: a task by hand, a commitment of the wearer, marked edited so a later
    /// summary never touches it. Read by hand, so a field of the wrong type is a 400 in every environment.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        JsonElement body, NpgsqlDataSource dataSource, TaskStore tasks, IEventPublisher events, TimeProvider time,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        string? text = null;
        DateTime? due = null;
        Guid? conversationId = null;
        Guid? personId = null;
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors["body"] = ["Send an object with text, due, conversationId or personId."];
        }
        else
        {
            if (body.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String)
            {
                text = textValue.GetString()!.Trim();
            }

            if (text is null || text.Length is 0 or > MaxTextLength)
            {
                errors["text"] = [$"The text must be 1 to {MaxTextLength} characters."];
            }
            else if (TextFingerprint.Of(text).Length == 0)
            {
                errors["text"] = ["The text must contain a letter or a digit."];
            }

            if (body.TryGetProperty("due", out var dueValue) && dueValue.ValueKind != JsonValueKind.Null
                && (dueValue.ValueKind != JsonValueKind.String || !TryParseDue(dueValue.GetString(), out due)))
            {
                errors["due"] = ["Must be an ISO 8601 date or time, or null."];
            }

            ReadId(body, "conversationId", errors, out conversationId);
            ReadId(body, "personId", errors, out personId);
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var created = await tasks.CreateAsync(
            connection, transaction, conversationId, text!, TextFingerprint.Of(text!), personId, TaskKinds.Commitment, due,
            time.GetUtcNow(), ct);
        switch (created.Problem)
        {
            case TaskCreateProblem.NoConversation:
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such conversation.");
            case TaskCreateProblem.NoPerson:
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such person.");
            case TaskCreateProblem.Duplicate:
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: TaskEndpoints.DuplicateTitle);
        }

        await events.PublishAsync(new NytkaEvent(NytkaEvent.TaskCreated, created.Task!.Id), connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return Results.Json(created.Task, statusCode: StatusCodes.Status201Created);
    }

    private static void ReadId(JsonElement body, string name, Dictionary<string, string[]> errors, out Guid? id)
    {
        id = null;
        if (!body.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var parsed))
        {
            id = parsed;
        }
        else
        {
            errors[name] = ["Must be an id or null."];
        }
    }

    /// <summary>Body <c>{ text?, done?, personId?, due? }</c>; a null <c>personId</c> clears the person and a null <c>due</c> the due date. Read by hand, so a field of the wrong type is a 400 in every environment.</summary>
    private static async Task<IResult> PatchAsync(
        Guid id, JsonElement body, NpgsqlDataSource dataSource, TaskStore tasks, IEventPublisher events, TimeProvider time,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        string? text = null;
        bool? done = null;
        PersonChange? person = null;
        DueChange? dueChange = null;
        SalienceChange? salience = null;
        if (body.ValueKind != JsonValueKind.Object)
        {
            errors["body"] = ["Send an object with text, done, personId, due or salience."];
        }
        else
        {
            if (!SalienceBody.TryRead(body, out salience))
            {
                errors["salience"] = [SalienceBody.Bad];
            }

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

            if (body.TryGetProperty("personId", out var personValue))
            {
                if (personValue.ValueKind == JsonValueKind.Null)
                {
                    person = new PersonChange(null);
                }
                else if (personValue.ValueKind == JsonValueKind.String && personValue.TryGetGuid(out var personId))
                {
                    person = new PersonChange(personId);
                }
                else
                {
                    errors["personId"] = ["Must be a person id or null."];
                }
            }

            if (body.TryGetProperty("due", out var dueValue))
            {
                if (dueValue.ValueKind == JsonValueKind.Null)
                {
                    dueChange = new DueChange(null);
                }
                else if (dueValue.ValueKind == JsonValueKind.String && TryParseDue(dueValue.GetString(), out var dueAt))
                {
                    dueChange = new DueChange(dueAt);
                }
                else
                {
                    errors["due"] = ["Must be an ISO 8601 date or time, or null."];
                }
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        TaskUpdate? update;
        try
        {
            if (salience is not null && !await TaskStore.SetSalienceAsync(connection, transaction, id, salience.Salience, time.GetUtcNow(), ct))
            {
                return NotFound();
            }

            update = await tasks.UpdateAsync(connection, transaction, id, text, done, person, dueChange, time.GetUtcNow(), ct);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such person.");
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Reopening a task made by hand while another open one has its text.
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: DuplicateTitle);
        }

        if (update is null)
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
