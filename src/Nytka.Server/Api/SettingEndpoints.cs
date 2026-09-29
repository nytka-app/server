using System.Text.Json;
using Nytka.Server.Settings;

namespace Nytka.Server.Api;

/// <summary>The settings endpoints (docs/specs/v0.2.md, track A): list and change. Both need <c>admin</c>.</summary>
public static class SettingEndpoints
{
    public static RouteGroupBuilder MapSettings(this RouteGroupBuilder api)
    {
        var settings = api.MapGroup("/settings");
        settings.MapGet("", List);
        settings.MapPatch("", PatchAsync);
        return api;
    }

    public sealed record PatchRequest(Dictionary<string, JsonElement>? Values);

    /// <summary>
    /// A setting as the API shows it. <paramref name="Value"/> is null for a secret and for a key nothing sets;
    /// <paramref name="IsSet"/> says whether the environment or the table supplies it, not the default.
    /// </summary>
    public sealed record Setting(
        string Key, string Type, string? Value, bool IsSet, string Source, bool Locked, string? Default);

    public sealed record SettingList(IReadOnlyList<Setting> Items);

    private static IResult List(SettingsService settings) => Results.Ok(Describe(settings));

    private static async Task<IResult> PatchAsync(HttpRequest http, SettingsService settings, CancellationToken ct)
    {
        var request = await TokenEndpoints.ReadBodyAsync<PatchRequest>(http, ct);
        if (request?.Values is not { } raw)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Send { \"values\": { \"<key>\": value or null } }.");
        }

        var values = new Dictionary<string, string?>();
        var errors = new Dictionary<string, string[]>();
        foreach (var (key, element) in raw)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    values[key] = element.GetString();
                    break;
                case JsonValueKind.Null:
                    values[key] = null;
                    break;
                default:
                    errors[key] = ["Must be a string or null."];
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        return await settings.UpdateAsync(values, ct) switch
        {
            SettingsUpdate.Invalid invalid => Results.ValidationProblem(invalid.Errors),
            SettingsUpdate.Locked locked => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "These settings are set by the server's environment and cannot be changed here.",
                detail: string.Join(", ", locked.Keys)),
            _ => Results.Ok(Describe(settings)),
        };
    }

    private static SettingList Describe(SettingsService settings) =>
        new(settings.Definitions.Select(definition =>
        {
            var resolved = settings.Resolve(definition);
            var locked = settings.IsLocked(definition);
            return definition.IsSecret
                ? new Setting(definition.Key, TypeName(definition.Type), null, resolved.Source == SettingSource.Env, SourceName(resolved.Source), locked, null)
                : new Setting(
                    definition.Key, TypeName(definition.Type), resolved.Value, resolved.Source != SettingSource.Default,
                    SourceName(resolved.Source), locked, definition.Default);
        }).ToList());

    private static string TypeName(SettingType type) => type.ToString().ToLowerInvariant();

    private static string SourceName(SettingSource source) => source.ToString().ToLowerInvariant();
}
