namespace Nytka.Server.Settings;

/// <summary>
/// The settings hooks (docs/specs/v0.2.md, track A). Both are stubs until then: options come from
/// the environment and the defaults, as in v0.1.
/// <para>
/// Track A must resolve settings through its own service or <c>IOptionsMonitor</c>, never a cached
/// <c>IOptions&lt;NytkaOptions&gt;</c>: Program.cs resolves that one before the migrator, when the
/// table may not exist, and it keeps its value. The layer must also drop empty environment values
/// before binding, so <c>Nytka__X=""</c> never shadows the table; the test for that is A's
/// (<c>SettingDefinition.Resolve</c> already has it).
/// </para>
/// </summary>
public static class SettingsExtensions
{
    /// <summary>
    /// Adds the table's layer to the configuration, below the environment. Runs before anything
    /// binds options. The table itself is read only after the migrator has run.
    /// </summary>
    public static IHostApplicationBuilder AddNytkaSettingsLayer(this IHostApplicationBuilder builder) => builder;

    public static IServiceCollection AddNytkaSettings(this IServiceCollection services) => services;
}
