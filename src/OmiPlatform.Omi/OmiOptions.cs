using System.ComponentModel.DataAnnotations;

namespace OmiPlatform.Omi;

public sealed class OmiOptions
{
    public const string SectionName = "Omi";

    /// <summary>Developer API key, "omi_dev_...". Create one in the Omi app under
    /// Settings -> Developer -> API Keys, with scopes `memories:read conversations:read`.
    /// Static and non-rotating — unlike Oura there is no OAuth flow here.</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    public Uri ApiBaseAddress { get; set; } = new("https://api.omi.me/v1/dev/");

    /// <summary>Earliest day the backfill asks for. Unlike Oura this has no sensible historical
    /// default — the necklace has no data before it existed. Set this to roughly when you started
    /// wearing it.</summary>
    [Required]
    public DateOnly BackfillFrom { get; set; }

    /// <summary>Days of already-ingested conversation history the reconcile job re-walks each
    /// cycle. Conversations can be edited or discarded in the Omi app after creation, so this is
    /// not optional — just shorter than Oura's, since Omi has no documented "lands the next
    /// morning" lag.</summary>
    public int ReconcileTrailingDays { get; set; } = 3;

    /// <summary>Page size for both list endpoints. Omi's own default is 25; kept explicit rather
    /// than relying on the API's default.</summary>
    public int PageSize { get; set; } = 25;

    /// <summary>Recorded on every raw document so a re-projection can tell which understanding of
    /// the API shape a payload was parsed under. Omi publishes no versioned OpenAPI spec the way
    /// Oura does, so this is the date the field names here were last checked against
    /// docs.omi.me — see docs/omi-api-notes.md.</summary>
    public string SpecVersion { get; set; } = "docs.omi.me, verified 2026-09-28";
}
