using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OmiPlatform.Mcp;
using OmiPlatform.Storage;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

// Storage only. This process never talks to Omi, so it needs no API key — the value is in the
// warehouse and its tool surface, not in proxying the phone app.
builder.Services.AddOmiStorage(builder.Configuration);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "omi-platform", Version = "0.1.0" };
        options.ServerInstructions = OmiTools.Instructions;
    })
    .WithHttpTransport()
    .WithToolsFromAssembly();

builder.Services.AddHealthChecks().AddCheck<WarehouseHealthCheck>("warehouse");

var app = builder.Build();

app.UseSerilogRequestLogging();
app.MapHealthChecks("/healthz");

// No authentication of its own: reachable only on the Compose network and through whatever fronts
// it. Do not publish this port beyond a private network.
app.MapMcp("/mcp");

app.Run();

/// <summary>Postgres reachability, and whether ingest has backfilled anything yet.</summary>
internal sealed class WarehouseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("select count(*) from conversations", connection);
            var conversations = (long?)await command.ExecuteScalarAsync(cancellationToken) ?? 0;

            return conversations > 0
                ? HealthCheckResult.Healthy($"{conversations} conversation(s) in the warehouse.")
                : HealthCheckResult.Degraded("The warehouse is empty; ingest has not backfilled yet.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Postgres is not reachable.", exception);
        }
    }
}
