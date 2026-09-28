using Microsoft.Extensions.Options;
using OmiPlatform.Storage;

namespace OmiPlatform.Ingest;

/// <summary>
/// The whole ingest loop: migrate, backfill once, then reconcile on a timer.
/// </summary>
/// <remarks>
/// Single instance, same as oura-ingest — not because of a single-use token here (Omi's API key
/// is static), but there is no reason to run two and every write is upsert-idempotent regardless.
/// </remarks>
public sealed class IngestWorker(
    IServiceProvider services,
    DatabaseMigrator migrator,
    IOptions<IngestOptions> options,
    ILogger<IngestWorker> logger) : BackgroundService
{
    private readonly IngestOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.MigrateOnStartup)
        {
            migrator.Run();
        }

        var backfilled = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!backfilled)
                {
                    await using var scope = services.CreateAsyncScope();
                    backfilled = await scope.ServiceProvider.GetRequiredService<BackfillJob>()
                        .RunAsync(stoppingToken).ConfigureAwait(false);
                }

                await using (var scope = services.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<ReconcileJob>()
                        .RunAsync(stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(_options.ReconcileInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Ingest cycle failed; retrying in {Delay}.", _options.RetryDelay);
                await Task.Delay(_options.RetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
