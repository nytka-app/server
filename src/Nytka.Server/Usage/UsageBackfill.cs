using Microsoft.Extensions.Options;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Usage;

/// <summary>
/// At start, gives every batch that finished before the ledger existed an estimated row: priced as the usual model is now,
/// since the server never recorded which engine answered. After the first start it finds nothing to add.
/// </summary>
public sealed class UsageBackfill(UsageStore usage, SettingsService settings, IOptions<NytkaOptions> options, ILogger<UsageBackfill> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var engine = UsageLedger.Engine(options.Value.Stt.Model);
        var added = await usage.BackfillAsync(engine, UsageSettings.Prices(settings).RateOf(engine), ct);
        if (added > 0)
        {
            logger.LogInformation("Usage: estimated {Batches} earlier batches as {Engine}", added, engine);
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
