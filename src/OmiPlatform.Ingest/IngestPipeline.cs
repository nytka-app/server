using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmiPlatform.Omi;
using OmiPlatform.Storage;

namespace OmiPlatform.Ingest;

/// <summary>
/// Fetches from Omi, writes to <c>omi_raw</c>, then projects. Raw first, always: if the projection
/// throws, the payload is already durable and can be replayed from the database without touching
/// the API again.
/// </summary>
public sealed class IngestPipeline
{
    private readonly IOmiApiClient _api;
    private readonly RawDocumentRepository _raw;
    private readonly DocumentProjector _projector;
    private readonly IngestWindowRepository _windows;
    private readonly OmiOptions _options;
    private readonly ILogger<IngestPipeline> _logger;

    public IngestPipeline(
        IOmiApiClient api,
        RawDocumentRepository raw,
        DocumentProjector projector,
        IngestWindowRepository windows,
        IOptions<OmiOptions> options,
        ILogger<IngestPipeline> logger)
    {
        _api = api;
        _raw = raw;
        _projector = projector;
        _windows = windows;
        _options = options.Value;
        _logger = logger;
    }

    /// <param name="recordProgress">Backfill bookkeeping. Only the backfill sets this — see
    /// <see cref="BackfillJob"/>.</param>
    public async Task<int> IngestConversationWindowAsync(DateWindow window, bool recordProgress, CancellationToken cancellationToken)
    {
        var documents = new List<OmiRawDocument>();
        await foreach (var document in _api.FetchConversationsAsync(window, cancellationToken))
        {
            documents.Add(document);
        }

        if (documents.Count > 0)
        {
            await _raw.UpsertAsync(documents, _options.SpecVersion, cancellationToken).ConfigureAwait(false);
            await _projector.ProjectAsync("conversation", documents, cancellationToken).ConfigureAwait(false);
        }

        if (recordProgress)
        {
            await _windows.RecordAsync("conversation", window, documents.Count, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("conversations {Window}: {Count} document(s).", window, documents.Count);
        return documents.Count;
    }

    /// <summary>Fetches every memory and upserts it. No windowing: the list endpoint takes no date
    /// filter, so a full re-list is how every cycle stays complete rather than "since last time".</summary>
    public async Task<int> IngestMemoriesAsync(CancellationToken cancellationToken)
    {
        var documents = new List<OmiRawDocument>();
        await foreach (var document in _api.FetchMemoriesAsync(cancellationToken))
        {
            documents.Add(document);
        }

        if (documents.Count > 0)
        {
            await _raw.UpsertAsync(documents, _options.SpecVersion, cancellationToken).ConfigureAwait(false);
            await _projector.ProjectAsync("memory", documents, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("memories: {Count} document(s).", documents.Count);
        return documents.Count;
    }

    /// <summary>Re-reads stored payloads and rebuilds the typed tables, without calling the API.
    /// This is what makes <c>omi_raw</c> the source of truth rather than a debugging aid.</summary>
    public async Task<int> ReprojectAsync(string docType, CancellationToken cancellationToken)
    {
        var stored = await _raw.ReadAsync(docType, cancellationToken).ConfigureAwait(false);
        var projected = await _projector.ProjectAsync(docType, stored, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Re-projected {Count} stored {DocType} document(s) into {Rows} row(s).", stored.Count, docType, projected);

        return projected;
    }
}
