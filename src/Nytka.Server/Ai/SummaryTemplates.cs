using Nytka.Server.Settings;

namespace Nytka.Server.Ai;

/// <summary>
/// The summary templates (NYTKA-82, docs/explanation/summary-templates.md): per kind of conversation, the instruction for what the
/// summary holds. Each is a setting, so an edit reaches the next job without a restart, and a template only ever replaces the
/// summary's wording in the prompt: the items, the tags, the output language and the rule against invented deadlines stay as they are.
/// </summary>
public sealed class SummarySettings : ISettingsGroup
{
    public const string EnabledKey = "summary.templates.enabled";

    public const int MaxTemplate = 600;

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "false", SettingValidators.Bool),
        .. SummaryTemplates.Ids.Select(id => new SettingDefinition(
            SummaryTemplates.Key(id), SettingType.String, SummaryTemplates.BuiltIn(id), Validate)),
    ];

    /// <summary>One line of plain text: a line break or control character would let a template forge another line of the prompt.</summary>
    private static string? Validate(string value) =>
        value.Any(char.IsControl) ? "Must be one line, without control characters."
        : SettingValidators.MaxLength(MaxTemplate)(value);
}

public static class SummaryTemplates
{
    public const string Default = "default";

    /// <summary>The kinds the model may name, and the tags that pick a template; anything else is <see cref="Default"/>.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["meeting", "lecture", "doctor", "call"];

    public static readonly IReadOnlyList<string> Ids = [Default, .. Kinds];

    public static string Key(string id) => "summary.templates." + id;

    public static bool IsKnown(string? id) => id is not null && Ids.Contains(id);

    /// <summary>
    /// What a template says until it is edited. The default is the wording the prompt always had, so a server nobody edited sends
    /// the same prompt as before. A template ends with the length it allows; the server cuts to <see cref="ConversationPrompt.MaxSummary"/> either way.
    /// </summary>
    public static string BuiltIn(string id) => id switch
    {
        "meeting" => $"the decisions made, who owns which next step and what is still open, in short lines; at most {ConversationPrompt.MaxSummary} characters",
        "lecture" => $"the topic, the main points in the order given and any term or example worth remembering; at most {ConversationPrompt.MaxSummary} characters",
        "doctor" => $"the complaint, what the doctor found or said, the treatment or advice given and what was agreed for next time; at most {ConversationPrompt.MaxSummary} characters",
        "call" => $"who called, what it was about and what was agreed; at most {ConversationPrompt.MaxSummary} characters",
        _ => ConversationPrompt.DefaultSummarySpec,
    };

    public static bool Enabled(SettingsService settings) => settings.Get(SummarySettings.EnabledKey) == "true";

    /// <summary>The template's text as the settings hold it now: the edited one, else the built-in.</summary>
    public static string Text(SettingsService settings, string id) => settings.Get(Key(id)) ?? BuiltIn(id);

    /// <summary>The first template, in the order of <see cref="Kinds"/>, that a tag of the conversation is named after.</summary>
    public static string? FromTags(IEnumerable<string> tags)
    {
        var held = tags.ToHashSet(StringComparer.Ordinal);
        return Kinds.FirstOrDefault(held.Contains);
    }
}
