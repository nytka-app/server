using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmiPlatform.Omi;

namespace OmiPlatform.Ingest;

/// <summary>
/// Re-fetches a trailing window of already-ingested conversation days, and fully re-lists memories.
/// </summary>
/// <remarks>
/// Conversations can be edited or discarded in the Omi app after creation, so re-walking a trailing
/// window and upserting is cheaper than reasoning about which days might have changed. Memories have
/// no date filter at all, so every cycle re-lists all of them — see CLAUDE.md.
/// </remarks>
public sealed class ReconcileJob
{
    private readonly IngestPipeline _pipeline;
    private readonly OmiOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ReconcileJob> _logger;

    public ReconcileJob(IngestPipeline pipeline, IOptions<OmiOptions> options, TimeProvider time, ILogger<ReconcileJob> logger)
    {
        _pipeline = pipeline;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var from = today.AddDays(-_options.ReconcileTrailingDays);

        var documents = 0;
        var failed = 0;

        try
        {
            foreach (var window in OmiWindows.SplitByDay(from, today))
            {
                documents += await _pipeline.IngestConversationWindowAsync(window, recordProgress: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed++;
            _logger.LogError(exception, "conversations: reconcile failed; will retry next cycle.");
        }

        try
        {
            documents += await _pipeline.IngestMemoriesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed++;
            _logger.LogError(exception, "memories: reconcile failed; will retry next cycle.");
        }

        _logger.LogInformation(
            "Reconciled conversations {From}..{To} + memories: {Count} document(s) upserted, {Failed} part(s) failed.",
            from, today, documents, failed);
    }
}
