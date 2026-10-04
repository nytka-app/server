namespace Nytka.Server.Settings;

/// <summary>How a setting is typed and edited. The API names the types in lower case: <c>url</c>, <c>int</c>, ...</summary>
public enum SettingType
{
    String,
    Url,
    Secret,
    Duration,
    Int,
    Language,
    Bool,
    Number,
}

/// <summary>Which layer supplied a setting's value. The API names them <c>default</c>, <c>db</c> and <c>env</c>.</summary>
public enum SettingSource
{
    Default,
    Db,
    Env,
}

public readonly record struct ResolvedSetting(string? Value, SettingSource Source);

/// <summary>
/// One key of the settings catalog. Values travel as the strings an environment variable would
/// carry, <paramref name="Default"/> included ("14", "true"). <paramref name="Validate"/> takes a
/// value that is set and returns what is wrong with it, or null.
/// </summary>
public sealed record SettingDefinition(string Key, SettingType Type, string? Default, Func<string, string?> Validate)
{
    /// <summary>A secret comes only from the environment: it is listed read-only and never written.</summary>
    public bool IsSecret => Type == SettingType.Secret;

    /// <summary>
    /// <c>Nytka__</c>, then the key with each <c>.</c> as <c>__</c> and each part capitalized:
    /// <c>llm.baseUrl</c> is <c>Nytka__Llm__BaseUrl</c>.
    /// </summary>
    public string EnvironmentVariable => "Nytka__" + string.Join("__", Key.Split('.').Select(Capitalize));

    /// <summary>
    /// Environment, else table, else default. An empty environment variable counts as unset, so a
    /// Compose file can pass every optional variable through empty without shadowing the table; a
    /// secret ignores the table, which never holds one. The table's rows are never empty (an empty
    /// <c>PATCH</c> value deletes the row), so an empty one counts as unset too.
    /// </summary>
    public ResolvedSetting Resolve(string? environment, string? table)
    {
        if (!string.IsNullOrEmpty(environment))
        {
            return new ResolvedSetting(environment, SettingSource.Env);
        }

        if (!IsSecret && !string.IsNullOrEmpty(table))
        {
            return new ResolvedSetting(table, SettingSource.Db);
        }

        return new ResolvedSetting(Default, SettingSource.Default);
    }

    private static string Capitalize(string part) => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..];
}

/// <summary>A set of related keys, registered as a singleton by whoever owns them; the settings service reads every group.</summary>
public interface ISettingsGroup
{
    IReadOnlyList<SettingDefinition> Definitions { get; }

    /// <summary>
    /// What is wrong with keys of the group that depend on each other, by key; <paramref name="value"/> resolves a key as
    /// the change would leave it. Empty when nothing is.
    /// </summary>
    IReadOnlyDictionary<string, string> Conflicts(Func<string, string?> value) => new Dictionary<string, string>();
}
