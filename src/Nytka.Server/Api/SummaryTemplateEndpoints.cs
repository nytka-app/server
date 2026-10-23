using Nytka.Server.Ai;
using Nytka.Server.Settings;

namespace Nytka.Server.Api;

/// <summary>
/// The summary templates (NYTKA-82): list, read, edit and reset. A template is the <c>summary.templates.{id}</c> setting, so these
/// routes are a typed view of it; the on/off switch is the <c>summary.templates.enabled</c> setting. All need <c>admin</c>.
/// </summary>
public static class SummaryTemplateEndpoints
{
    public static RouteGroupBuilder MapSummaryTemplates(this RouteGroupBuilder api)
    {
        var templates = api.MapGroup("/summary-templates");
        templates.MapGet("", (SettingsService settings) => Results.Ok(Describe(settings)));
        templates.MapGet("/{id}", (string id, SettingsService settings) =>
            SummaryTemplates.IsKnown(id) ? Results.Ok(One(settings, id)) : NotFound());
        templates.MapPut("/{id}", PutAsync);
        templates.MapDelete("/{id}", (string id, SettingsService settings, CancellationToken ct) => ApplyAsync(id, null, settings, ct));
        return api;
    }

    /// <summary>
    /// <paramref name="Text"/> is what the template says now; <paramref name="BuiltIn"/> what it says unedited; <paramref name="Edited"/>
    /// whether the server's table or environment overrides the built-in; <paramref name="Locked"/> that the environment sets it, so it cannot be changed here.
    /// </summary>
    public sealed record Template(string Id, string Text, string BuiltIn, bool Edited, bool Locked);

    /// <summary><paramref name="Enabled"/> is the <c>summary.templates.enabled</c> setting: off, only the default template is used.</summary>
    public sealed record TemplateList(bool Enabled, int MaxLength, IReadOnlyList<Template> Items);

    public sealed record PutRequest(string? Text);

    private static TemplateList Describe(SettingsService settings) =>
        new(SummaryTemplates.Enabled(settings), SummarySettings.MaxTemplate, SummaryTemplates.Ids.Select(id => One(settings, id)).ToList());

    private static Template One(SettingsService settings, string id)
    {
        var definition = settings.Find(SummaryTemplates.Key(id))!;
        return new Template(
            id, SummaryTemplates.Text(settings, id), SummaryTemplates.BuiltIn(id),
            settings.Resolve(definition).Source != SettingSource.Default, settings.IsLocked(definition));
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"Templates are: {string.Join(", ", SummaryTemplates.Ids)}.");

    /// <summary>Body <c>{ text }</c>: 1 to <see cref="SummarySettings.MaxTemplate"/> characters.</summary>
    private static async Task<IResult> PutAsync(string id, HttpRequest http, SettingsService settings, CancellationToken ct)
    {
        if (!SummaryTemplates.IsKnown(id))
        {
            return NotFound();
        }

        var body = await TokenEndpoints.ReadBodyAsync<PutRequest>(http, ct);
        if (string.IsNullOrWhiteSpace(body?.Text))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = ["Send the template as a non-empty string; DELETE restores the built-in one."] });
        }

        return await ApplyAsync(id, body.Text, settings, ct);
    }

    private static async Task<IResult> ApplyAsync(string id, string? text, SettingsService settings, CancellationToken ct)
    {
        if (!SummaryTemplates.IsKnown(id))
        {
            return NotFound();
        }

        var key = SummaryTemplates.Key(id);
        return await settings.UpdateAsync(new Dictionary<string, string?> { [key] = text }, ct) switch
        {
            SettingsUpdate.Invalid invalid => Results.ValidationProblem(invalid.Errors.ToDictionary(e => "text", e => e.Value)),
            SettingsUpdate.Locked => Results.Problem(
                statusCode: StatusCodes.Status409Conflict, title: "This template is set by the server's environment and cannot be changed here.", detail: key),
            _ => Results.Ok(One(settings, id)),
        };
    }
}
