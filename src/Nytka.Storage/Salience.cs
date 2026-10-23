namespace Nytka.Storage;

/// <summary>
/// How much an extracted task, note or memory matters, as the model judged it: <c>vital</c> is worth the day's screen,
/// <c>ordinary</c> is kept and listed, <c>noise</c> is kept but never shown first. Null for items made before salience or by hand.
/// </summary>
public static class Salience
{
    public const string Vital = "vital";
    public const string Ordinary = "ordinary";
    public const string Noise = "noise";

    /// <summary>The model's value when it is one of the three, else null: a model that strays from the schema costs the field, not the item.</summary>
    public static string? Parse(string? value) => value?.Trim().ToLowerInvariant() is Vital or Ordinary or Noise ? value.Trim().ToLowerInvariant() : null;

    /// <summary>The higher of two values (vital, then ordinary, then noise, then null).</summary>
    public static string? Max(string? a, string? b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>SQL for the rank of a salience expression: 3 vital, 2 ordinary, 1 noise, 0 null.</summary>
    public static string RankOf(string sql) => $"(case {sql} when 'vital' then 3 when 'ordinary' then 2 when 'noise' then 1 else 0 end)";

    private static int Rank(string? value) => value switch
    {
        Vital => 3,
        Ordinary => 2,
        Noise => 1,
        _ => 0,
    };
}
