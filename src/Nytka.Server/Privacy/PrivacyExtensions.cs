using Nytka.Server.Settings;

namespace Nytka.Server.Privacy;

/// <summary>The privacy hook: which sensitive classes untrusted readers leave out, and who is reading.</summary>
public static class PrivacyExtensions
{
    public static IServiceCollection AddNytkaPrivacy(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, PrivacySettings>();
        services.AddHttpContextAccessor();
        services.AddScoped<SensitivityScope>();
        return services;
    }
}
