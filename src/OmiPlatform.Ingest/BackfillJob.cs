using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmiPlatform.Omi;
using OmiPlatform.Storage;

namespace OmiPlatform.Ingest;

/// <summary>
/// Walks conversations day by day from <see cref="OmiOptions.BackfillFrom"/> to today, then fetches
/// every memory once. Idempotent and resumable: completed conversation days are recorded in
/// <c>ingest_window</c> and skipped on the next run, and every write is an upsert.
/// </summary>
public sealed class BackfillJob
{
    private readonly IngestPipeline _pipeline;
    private readonly IngestWindowRepository _windows;
    private readonly OmiOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BackfillJob> _logger;

    public BackfillJob(
        IngestPipeline pipeline,
        IngestWindowRepository windows,
        IOptions<OmiOptions> options,
        TimeProvider time,
        ILogger<BackfillJob> logger)
    {
        _pipeline = pipeline;
        _windows = windows;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Returns false if any part failed, in which case the caller should run the backfill
    /// again rather than treating history as complete.</summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        var complete = true;

        try
        {
            await BackfillConversationsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            complete = false;
            _logger.LogError(exception, "conversations: backfill failed; will retry next cycle.");
        }

        try
        {
            var count = await _pipeline.IngestMemoriesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("memories: fetched {Count}.", count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            complete = false;
            _logger.LogError(exception, "memories: backfill failed; will retry next cycle.");
        }

        return complete;
    }

    private async Task BackfillConversationsAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var completed = await _windows.CompletedWindowStartsAsync("conversation", cancellationToken).ConfigureAwait(false);

        // Newest first: a backfill interrupted on day one still leaves the most useful data behind.
        var pending = OmiWindows.SplitByDay(_options.BackfillFrom, today)
            .Where(window => !completed.Contains(window.Start))
            .OrderByDescending(window => window.Start)
            .ToArray();

        if (pending.Length == 0)
        {
            _logger.LogDebug("conversations: backfill already complete.");
            return;
        }

        _logger.LogInformation("conversations: backfilling {Count} day(s) from {From}.", pending.Length, _options.BackfillFrom);

        var documents = 0;
        foreach (var window in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents += await _pipeline.IngestConversationWindowAsync(window, recordProgress: true, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("conversations: backfill complete, {Count} document(s).", documents);
    }
}
