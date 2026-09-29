using Nytka.Storage;

namespace Nytka.Server.Search;

/// <summary>
/// The search indexer. At start, in the background, it runs <c>nytka_search_setup()</c> so the configuration
/// follows the dictionary files, then fills the <c>search</c> vectors of new and changed rows in batches. Writes
/// never touch the configuration, so a dictionary that goes missing at run time fails only this loop: it logs, runs
/// the setup again (which falls back to <c>simple</c> for new vectors) and retries.
/// </summary>
public sealed partial class SearchDictionarySync(SearchStore search, ILogger<SearchDictionarySync> logger) : BackgroundService
{
    public const int BatchSize = 500;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hosted services start after the migrator, but the start must not wait for the indexing.
        await Task.Yield();
        string? mode = null;
        var setup = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (setup)
                {
                    var now = await search.SetupDictionaryAsync(stoppingToken);
                    if (now != mode)
                    {
                        if (now == "uk")
                        {
                            LogLoaded(logger);
                        }
                        else
                        {
                            LogFallback(logger);
                        }
                    }

                    mode = now;
                    setup = false;
                }

                if (await search.IndexPendingAsync(BatchSize, stoppingToken) == 0)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex);
                // Look at the files again before the next try: a lost dictionary becomes simple.
                setup = true;
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(LogLevel.Information, "Search: the Ukrainian dictionary is loaded.")]
    private static partial void LogLoaded(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Search: the Ukrainian dictionary is not loaded (uk_ua.dict and uk_ua.affix are missing from tsearch_data); Cyrillic words match exactly. See scripts/fetch-uk-dictionary.sh.")]
    private static partial void LogFallback(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Search: indexing failed; will look at the dictionary again and retry.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
