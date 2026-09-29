using Microsoft.Extensions.Options;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Ai;

/// <summary>The AI layer's hook (docs/specs/v0.2.md, track B): the model client, the enrich job and the scheduler's scan.</summary>
public static class AiExtensions
{
    public static IServiceCollection AddNytkaAi(this IServiceCollection services)
    {
        services.AddOptions<LlmOptions>()
            .BindConfiguration(LlmOptions.Section)
            .Validate(
                o => o.JsonMode.Equals(LlmOptions.SchemaMode, StringComparison.OrdinalIgnoreCase)
                    || o.JsonMode.Equals(LlmOptions.ObjectMode, StringComparison.OrdinalIgnoreCase),
                "Nytka__Llm__JsonMode must be schema or object.")
            .Validate(o => o.TimeoutSeconds > 0, "Nytka__Llm__TimeoutSeconds must be positive.")
            .Validate(o => o.MaxInputChars > 0, "Nytka__Llm__MaxInputChars must be positive.")
            .Validate(o => o.BackfillDays >= 0, "Nytka__Llm__BackfillDays cannot be negative.")
            .ValidateOnStart();

        services.AddSingleton<ISettingsGroup, LlmSettings>();
        services.AddSingleton<LlmOptionsSetup>();
        services.AddSingleton<IPostConfigureOptions<LlmOptions>>(p => p.GetRequiredService<LlmOptionsSetup>());
        services.AddSingleton<IOptionsChangeTokenSource<LlmOptions>>(p => p.GetRequiredService<LlmOptionsSetup>());

        // The per-request timeout lives in LlmClient, where it can follow the option.
        services.AddHttpClient<ILlmClient, LlmClient>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<EnrichmentQueue>();
        services.AddScoped<IJobHandler, EnrichConversationHandler>();
        return services;
    }
}
