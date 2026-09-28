using OmiPlatform.Storage;

namespace OmiPlatform.Ingest;

/// <summary>
/// <c>--reproject [conversation|memory ...]</c> rebuilds the typed tables from <c>omi_raw</c> and
/// exits, without touching the API. Reachable after a schema change, or after restoring a warehouse
/// dump of <c>omi_raw</c> alone.
/// </summary>
public static class ReprojectCommand
{
    public const string Flag = "--reproject";

    private static readonly string[] AllDocTypes = ["conversation", "memory"];

    public static async Task<int> RunAsync(IHost host, string[] args, CancellationToken cancellationToken)
    {
        var logger = host.Services.GetRequiredService<ILogger<IngestWorker>>();
        var migrator = host.Services.GetRequiredService<DatabaseMigrator>();

        // A restored dump predates whatever migrations have been added since; project against the
        // current schema or the upserts reference columns that are not there yet.
        migrator.Run();

        var requested = args.Where(a => a != Flag).ToArray();
        var docTypes = requested.Length == 0 ? AllDocTypes : requested;

        var pipeline = host.Services.GetRequiredService<IngestPipeline>();

        var total = 0;
        foreach (var docType in docTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += await pipeline.ReprojectAsync(docType, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Re-projected {DocTypes} into {Rows} row(s). Nothing was fetched from Omi.",
            string.Join(", ", docTypes), total);

        return 0;
    }
}
