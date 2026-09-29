using System.Globalization;
using Microsoft.Extensions.Options;

namespace Nytka.Server.Settings;

/// <summary>
/// Lays the resolved settings over <see cref="NytkaOptions"/> after its configuration binding: an empty
/// environment variable that the binder left behind never shadows the table, and a table row or a
/// default fills what the environment does not set. The admin token stays a plain option.
/// </summary>
public sealed class SettingsOptionsSetup(SettingsService settings) : IPostConfigureOptions<NytkaOptions>
{
    public void PostConfigure(string? name, NytkaOptions options)
    {
        options.Stt.Url = settings.Get("stt.url") ?? "";
        options.Stt.ApiKey = settings.Get("stt.apiKey");
        options.Stt.Model = settings.Get("stt.model");
        options.Stt.Language = settings.Get("stt.language");
        options.Conversations.Gap = Duration(settings.Get("conversations.gap"), options.Conversations.Gap);
        options.Audio.RetentionDays = settings.Get("audio.retentionDays") is { } days
            ? int.Parse(days, CultureInfo.InvariantCulture)
            : options.Audio.RetentionDays;
    }

    private static TimeSpan Duration(string? value, TimeSpan fallback) =>
        value is not null && SettingValidators.TryParseDuration(value, out var duration) ? duration : fallback;
}

/// <summary>
/// <c>IOptions&lt;T&gt;</c> as a live view of <c>IOptionsMonitor&lt;T&gt;</c>: v0.1's handlers take
/// <c>IOptions&lt;NytkaOptions&gt;</c>, and its built-in implementation would keep the value it built first.
/// </summary>
public sealed class LiveOptions<T>(IOptionsMonitor<T> monitor) : IOptions<T>
    where T : class
{
    public T Value => monitor.CurrentValue;
}

/// <summary>Reads the settings table once the migrator has run, before any other hosted service starts.</summary>
public sealed class SettingsLoader(SettingsService settings) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => settings.ReloadAsync(ct);

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
