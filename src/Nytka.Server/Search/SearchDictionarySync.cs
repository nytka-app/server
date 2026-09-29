using Nytka.Storage;

namespace Nytka.Server.Search;

/// <summary>
/// At every start, in the background, runs <c>nytka_search_setup()</c> so the search configuration follows the
/// dictionary files: mounted later, it switches to the dictionary; removed later, it falls back to <c>simple</c>
/// instead of failing every insert of a Cyrillic segment.
/// </summary>
public sealed partial class SearchDictionarySync(SearchStore search, ILogger<SearchDictionarySync> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hosted services start after the migrator, but the start must not wait for a table rewrite.
        await Task.Yield();
        try
        {
            var mode = await search.SetupDictionaryAsync(stoppingToken);
            if (mode == "uk")
            {
                LogLoaded(logger);
            }
            else
            {
                LogFallback(logger);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(LogLevel.Information, "Search: the Ukrainian dictionary is loaded.")]
    private static partial void LogLoaded(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Search: the Ukrainian dictionary is not loaded (uk_ua.dict and uk_ua.affix are missing from tsearch_data); Cyrillic words match exactly. See scripts/fetch-uk-dictionary.sh.")]
    private static partial void LogFallback(ILogger logger);

    [LoggerMessage(LogLevel.Error, "Search: setting up the dictionary failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
