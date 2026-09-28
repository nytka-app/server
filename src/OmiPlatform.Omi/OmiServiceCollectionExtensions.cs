using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace OmiPlatform.Omi;

public static class OmiServiceCollectionExtensions
{
    public static IServiceCollection AddOmiApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OmiOptions>()
            .Bind(configuration.GetSection(OmiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var client = services.AddHttpClient<IOmiApiClient, OmiApiClient>(OmiApiClient.HttpClientName, (provider, http) =>
        {
            var options = provider.GetRequiredService<IOptions<OmiOptions>>().Value;
            http.BaseAddress = options.ApiBaseAddress;
            http.Timeout = TimeSpan.FromSeconds(60);
            // Static key, never rotates: unlike Oura there is no refresh flow, so the header is
            // set once here rather than through a per-request auth handler.
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        });

        client.AddResilienceHandler("omi", (builder, context) =>
        {
            var logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("OmiPlatform.Omi.Resilience");

            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 6,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = static arguments => ValueTask.FromResult(
                    arguments.Outcome.Exception is HttpRequestException or TimeoutException ||
                    arguments.Outcome.Result is { } response && IsTransient(response.StatusCode)),

                // 100 requests/minute per key, documented at docs.omi.me. Retry-After is obeyed
                // when Omi sends it; otherwise exponential backoff covers it.
                DelayGenerator = static arguments =>
                {
                    var retryAfter = arguments.Outcome.Result?.Headers.RetryAfter;
                    var delay = retryAfter?.Delta
                        ?? (retryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null);

                    return ValueTask.FromResult<TimeSpan?>(
                        delay is { } value && value > TimeSpan.Zero ? value : null);
                },

                OnRetry = arguments =>
                {
                    logger.LogWarning(
                        "Omi request failed ({Status}); retry {Attempt} in {Delay}.",
                        arguments.Outcome.Result?.StatusCode is { } status
                            ? ((int)status).ToString()
                            : arguments.Outcome.Exception?.GetType().Name,
                        arguments.AttemptNumber + 1,
                        arguments.RetryDelay);

                    return ValueTask.CompletedTask;
                },
            });

            builder.AddTimeout(TimeSpan.FromSeconds(90));
        });

        return services;
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}
