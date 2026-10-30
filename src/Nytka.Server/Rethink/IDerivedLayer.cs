using Nytka.Storage;

namespace Nytka.Server.Rethink;

/// <summary>What a layer's version is made of. Each part is hashed; <paramref name="Settings"/> maps a setting's key to its effective value.</summary>
/// <param name="Model">The model that makes the rows (the speaker model, the text model), or empty.</param>
/// <param name="Prompt">The prompt text, or for a layer with no model call its rules revision.</param>
public sealed record LayerInputs(string Model, string Prompt, IReadOnlyDictionary<string, string?> Settings);

/// <summary>
/// A derived layer: rows the server makes from stored segments, audio and the owner's answers, which a better model, prompt, rule or
/// setting makes better (docs/explanation/features.md, Nightly re-think). A layer records the version that made its rows, says which
/// days are stale, and knows how to rebuild one day. A rebuild never touches segments, audio, the owner's answers or golden labels.
/// This is the one seam for a new layer: implement it, register it as a singleton, and the version records, <c>GET /api/v1/quality</c>
/// and the nightly job take it in (facts, nytka-app/server#305, will).
/// </summary>
public interface IDerivedLayer
{
    string Name { get; }

    /// <summary>
    /// The layers whose rows this one's follow, read on each call so that it may depend on settings. A new version of any of them is a
    /// new version of this one, and a day is rebuilt from the lowest stale layer up.
    /// </summary>
    IReadOnlyList<string> DependsOn { get; }

    /// <summary>Whether the layer is on. A layer that is off has no stale days.</summary>
    bool Enabled { get; }

    /// <summary>Whether a rebuild calls the text model, and so spends; any other costs CPU only.</summary>
    bool CallsModel { get; }

    LayerInputs Inputs();

    /// <summary>The local days with a row of an older version that a rebuild can change, oldest first.</summary>
    Task<IReadOnlyList<DateOnly>> StaleDaysAsync(LayerVersion version, CancellationToken ct);

    /// <summary>What a rebuild of the day would send to the model and the most it would get back; zero for a layer with no model call.</summary>
    Task<LayerChars> EstimateAsync(DateOnly day, LayerVersion version, CancellationToken ct);

    /// <summary>
    /// Queues the rebuild of the day: the layer's own job, which stamps the rows with the version current when it runs. It changes
    /// nothing a reader sees until that job writes.
    /// </summary>
    Task RebuildAsync(DateOnly day, LayerVersion version, CancellationToken ct);
}
