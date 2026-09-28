using OmiPlatform.Ingest;
using OmiPlatform.Omi;
using OmiPlatform.Storage;
using Serilog;
using Serilog.Formatting.Compact;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

builder.Services.Configure<IngestOptions>(builder.Configuration.GetSection(IngestOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOmiApi(builder.Configuration);
builder.Services.AddOmiStorage(builder.Configuration);

builder.Services.AddScoped<IngestPipeline>();
builder.Services.AddScoped<BackfillJob>();
builder.Services.AddScoped<ReconcileJob>();

// `--reproject [conversation|memory ...]` rebuilds the typed tables from omi_raw and exits, without
// touching the API. Registering the worker in that mode would start the ingest loop alongside it.
var reproject = args.Contains(ReprojectCommand.Flag);
if (!reproject)
{
    builder.Services.AddHostedService<IngestWorker>();
}

var host = builder.Build();

if (reproject)
{
    return await ReprojectCommand.RunAsync(host, args, CancellationToken.None);
}

host.Run();
return 0;
