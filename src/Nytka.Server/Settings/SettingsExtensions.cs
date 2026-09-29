namespace Nytka.Server.Settings;

/// <summary>
/// The settings hooks (docs/specs/v0.2.md, track A). Both are stubs until then: options come from
/// the environment and the defaults, as in v0.1.
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
