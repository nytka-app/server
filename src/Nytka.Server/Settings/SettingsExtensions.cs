using Microsoft.Extensions.Options;

namespace Nytka.Server.Settings;

/// <summary>
/// The settings hooks (docs/specs/v0.2.md, track A). Options never come from a cached
/// <c>IOptions&lt;NytkaOptions&gt;</c>: Program.cs resolves that one before the migrator, when the table
/// may not exist, and the built-in one would keep its value. Here <c>IOptions&lt;NytkaOptions&gt;</c> is a
/// live view of the options monitor, which the settings service rebuilds whenever the table changes.
/// </summary>
public static class SettingsExtensions
{
    /// <summary>
    /// Puts the settings layer between the environment and the defaults, before anything
    /// binds options. The table itself is read only after the migrator has run, by <see cref="SettingsLoader"/>,
    /// which is registered here so it starts before every other hosted service. An empty environment value
    /// counts as unset, so it is hidden from the options binder as well. Until the table is read, and on a
    /// database without a <c>settings</c> table, the environment and the defaults apply.
    /// </summary>
    public static IHostApplicationBuilder AddNytkaSettingsLayer(this IHostApplicationBuilder builder)
    {
        // Empty values must never reach the binder: wrap every source that is already in.
        var sources = builder.Configuration.Sources;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is not NonEmptyConfigurationSource)
            {
                sources[i] = new NonEmptyConfigurationSource(sources[i]);
            }
        }

        var services = builder.Services;
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IOptionsChangeTokenSource<NytkaOptions>>(p => p.GetRequiredService<SettingsService>());
        services.AddSingleton<IPostConfigureOptions<NytkaOptions>, SettingsOptionsSetup>();
        services.AddSingleton<IOptions<NytkaOptions>>(p => new LiveOptions<NytkaOptions>(p.GetRequiredService<IOptionsMonitor<NytkaOptions>>()));
        services.AddHostedService<SettingsLoader>();
        return builder;
    }

    /// <summary>Adds the keys v0.1 already had to the catalog; every other track adds its own <see cref="ISettingsGroup"/>.</summary>
    public static IServiceCollection AddNytkaSettings(this IServiceCollection services) =>
        services.AddSingleton<ISettingsGroup, CoreSettings>();
}
