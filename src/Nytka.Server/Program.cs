using Microsoft.Extensions.Options;
using Nytka.Server;
using Nytka.Server.Api;
using Nytka.Server.Auth;
using Nytka.Storage;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logging) => logging
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

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

var app = builder.Build();

// Validate settings before the migrator touches the database; ValidateOnStart only runs in app.Run().
_ = app.Services.GetRequiredService<IOptions<NytkaOptions>>().Value;

var connectionString = app.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings__Postgres is required.");
new DatabaseMigrator(connectionString, app.Services.GetRequiredService<ILogger<DatabaseMigrator>>()).Run();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapHealth();

var api = app.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();
api.MapInfo();
api.MapChunks();

app.Run();

public partial class Program;
