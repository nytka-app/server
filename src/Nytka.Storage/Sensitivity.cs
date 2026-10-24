namespace Nytka.Storage;

/// <summary>
/// What a conversation or an item is about when it is private: <c>none</c>, <c>health</c>, <c>therapy</c>, <c>intimate</c> or
/// <c>money</c>, as the model judged it; null until classified, which readers treat as <c>none</c>.
/// </summary>
public static class Sensitivity
{
    public const string None = "none";

    public static readonly IReadOnlyList<string> Classes = ["health", "therapy", "intimate", "money"];

    /// <summary>The model's value when it is a known one, else null: a stray value costs the field, never the item.</summary>
    public static string? Parse(string? value) =>
        value?.Trim().ToLowerInvariant() is { } v && (v == None || Classes.Contains(v)) ? v : null;

    /// <summary>
    /// The more private of two values: a class beats <c>none</c>, which beats null; of two classes the first stays. A note is as
    /// private as its most private tip.
    /// </summary>
    public static string? Stronger(string? a, string? b) =>
        a is not (null or None) ? a : b is not (null or None) ? b : a ?? b;

    /// <summary>SQL for whether the row aliased <paramref name="alias"/> may be shown to a reader that hides <c>@hidden</c> (a <c>text[]</c>).</summary>
    public static string Visible(string alias) => $"({alias}.sensitivity is null or not ({alias}.sensitivity = any(@hidden)))";
}
