using Microsoft.Extensions.Options;
using Nytka.Audio.Vad;
using Nytka.Server;
using Nytka.Server.Ai;
using Nytka.Server.Api;
using Nytka.Server.Ask;
using Nytka.Server.Auth;
using Nytka.Server.Digests;
using Nytka.Server.Events;
using Nytka.Server.Import;
using Nytka.Server.Jobs;
using Nytka.Server.Mcp;
using Nytka.Server.Memories;
using Nytka.Server.Pipeline;
using Nytka.Server.Search;
using Nytka.Server.Settings;
using Nytka.Server.Transcription;
using Nytka.Server.Webhooks;
using Nytka.Storage;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logging) => logging
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

// The settings layer goes in before anything binds options: environment, then table, then defaults.
builder.AddNytkaSettingsLayer();

builder.Services.AddOptions<NytkaOptions>()
    .Bind(builder.Configuration.GetSection(NytkaOptions.Section))
    .Validate(o => o.AdminToken.Length >= 32, "Nytka__AdminToken must be at least 32 characters.")
    .Validate(o => Uri.TryCreate(o.Stt.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https",
        "Nytka__Stt__Url must be an absolute http or https URL.")
    .Validate(o => o.Conversations.Gap > TimeSpan.Zero, "Nytka__Conversations__Gap must be positive.")
    .Validate(o => o.Audio.RetentionDays >= 0, "Nytka__Audio__RetentionDays cannot be negative.")
    .ValidateOnStart();

builder.Services.AddNytkaStorage();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddSingleton<Scheduler>();
builder.Services.AddHostedService<JobRunnerService>();
builder.Services.AddHostedService<SchedulerService>();
builder.Services.AddHttpClient<TranscriptionClient>(client => client.Timeout = TranscriptionClient.Timeout);
builder.Services.AddHttpClient<VoiceprintClient>(client => client.Timeout = VoiceprintClient.Timeout)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// One model for the process; the Audio lane runs one job at a time, and Silero is not thread-safe.
builder.Services.AddSingleton<IVoiceActivityDetector>(_ => new SileroVad());
builder.Services.AddScoped<IJobHandler, ProcessSessionHandler>();
builder.Services.AddScoped<IJobHandler, TranscribeHandler>();
builder.Services.AddScoped<IJobHandler, CloseConversationsHandler>();
builder.Services.AddScoped<IJobHandler, RetentionHandler>();

// Every feature hangs off one hook, in this order. A hook is filled in where it lives, never here.
builder.Services.AddNytkaEvents();
builder.Services.AddNytkaAuth();
builder.Services.AddNytkaSettings();
builder.Services.AddNytkaAi();
builder.Services.AddNytkaAsk();
builder.Services.AddNytkaMcp();
builder.Services.AddNytkaMemories();
builder.Services.AddNytkaSearch();
builder.Services.AddNytkaDigests();
builder.Services.AddNytkaWebhooks();

var app = builder.Build();

// Validate settings before the migrator touches the database. The settings table may not exist yet, so
// this sees the environment and the defaults only. ValidateOnStart only runs in app.Run().
_ = app.Services.GetRequiredService<IOptions<NytkaOptions>>().Value;

var connectionString = app.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings__Postgres is required.");
new DatabaseMigrator(connectionString, app.Services.GetRequiredService<ILogger<DatabaseMigrator>>()).Run();

// The settings table exists from here on. Hosted services start in app.Run(), so their first read is safe.
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseNytkaAuth();
app.MapHealth();

var api = app.MapGroup("/api/v1").RequireNytkaAuth();
api.MapInfo();
api.MapChunks();
api.MapConversations();
api.MapAudio();
api.MapStatus();
api.MapDiagnostics();
api.MapCoverage();
api.MapTokens();
api.MapSettings();
api.MapTasks();
api.MapMemories();
api.MapSearch();
api.MapAsk();
api.MapWebhooks();
api.MapPeople();
api.MapImport();
api.MapBookmarks();
api.MapDigests();
api.MapExport();
app.MapNytkaMcp();

app.Run();

public partial class Program;
