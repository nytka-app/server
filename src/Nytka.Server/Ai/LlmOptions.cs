namespace Nytka.Server.Ai;

/// <summary>
/// Bound from <c>Nytka:Llm</c> (environment variables <c>Nytka__Llm__...</c>). Read through
/// <c>IOptionsMonitor</c>, so a changed <c>llm.model</c> reaches the next job without a restart.
/// </summary>
public sealed class LlmOptions
{
    public const string Section = "Nytka:Llm";

    public const string SchemaMode = "schema";

    public const string ObjectMode = "object";

    /// <summary>The endpoint's base URL; the server calls <c>{BaseUrl}/chat/completions</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Environment only. Empty for a local server.</summary>
    public string? ApiKey { get; set; }

    public string? Model { get; set; }

    /// <summary><c>auto</c> follows the conversation's language; otherwise a language tag such as <c>uk</c>.</summary>
    public string OutputLanguage { get; set; } = "auto";

    /// <summary><see cref="SchemaMode"/> sends a strict <c>json_schema</c>; <see cref="ObjectMode"/> a <c>json_object</c> with the schema in the prompt.</summary>
    public string JsonMode { get; set; } = SchemaMode;

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>The transcript size per window, in characters.</summary>
    public int MaxInputChars { get; set; } = 100_000;

    /// <summary>How far back unprocessed conversations are summarized.</summary>
    public int BackfillDays { get; set; } = 7;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}
