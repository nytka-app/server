using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Nytka.Server.Settings;

namespace Nytka.Server.Ai;

/// <summary>
/// Lays the resolved <c>llm.*</c> settings (environment, else table, else default) over
/// <see cref="LlmOptions"/> after its configuration binding, and re-reads them whenever a setting
/// changes, so a new <c>llm.model</c> reaches the next job without a restart. The environment-only
/// variables (<c>JsonMode</c>, <c>TimeoutSeconds</c>, ...) stay as bound.
/// </summary>
public sealed class LlmOptionsSetup(SettingsService settings) : IPostConfigureOptions<LlmOptions>, IOptionsChangeTokenSource<LlmOptions>
{
    public string? Name => Options.DefaultName;

    public void PostConfigure(string? name, LlmOptions options)
    {
        options.BaseUrl = settings.Get("llm.baseUrl");
        options.Model = settings.Get("llm.model");
        options.OutputLanguage = settings.Get("llm.outputLanguage") ?? options.OutputLanguage;
        options.ApiKey = settings.Get("llm.apiKey");
    }

    /// <summary>Follows the settings service, which signals every change to the catalog.</summary>
    public IChangeToken GetChangeToken() => ((IOptionsChangeTokenSource<NytkaOptions>)settings).GetChangeToken();
}
