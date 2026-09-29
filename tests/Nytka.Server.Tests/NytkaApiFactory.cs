using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nytka.Audio.Vad;
using Nytka.Server.Jobs;
using Nytka.Server.Transcription;

namespace Nytka.Server.Tests;

/// <summary>
/// The real server on a real (Testcontainers) database, with a fake clock. The job runners and the
/// scheduler do not run on their own: tests call <see cref="RunJobsAsync"/>, which runs every lane,
/// and <see cref="Scheduler.TickAsync"/> when they want them. A test extends the host through
/// <c>configure</c> (settings, as environment variables would supply them) and <c>services</c>.
/// </summary>
public sealed class NytkaApiFactory(
    PostgresFixture db,
    Action<IDictionary<string, string?>>? configure = null,
    Action<IServiceCollection>? services = null)
    : WebApplicationFactory<Program>
{
    public const string Token = "test-admin-token-0123456789-abcdefghij";

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));

    public FakeStt Stt { get; } = new();

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    /// <summary>
    /// A client whose bearer token is a new named token of <paramref name="scope"/> (<c>admin</c> or
    /// <c>read</c>). Its row goes straight into <c>api_tokens</c>, hashed as the API stores tokens.
    /// </summary>
    public HttpClient CreateClientWithScope(string scope)
    {
        var token = "nyt_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var id = Guid.CreateVersion7(Time.GetUtcNow());
        using (var connection = Get<NpgsqlDataSource>().OpenConnection())
        {
            connection.Execute(
                """
                insert into api_tokens (id, name, scope, token_hash, hint, created_at)
                values (@id, @name, @scope, @hash, @hint, @now)
                """,
                new
                {
                    id,
                    name = $"test-{scope}-{id:N}",
                    scope,
                    hash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
                    hint = token[^4..],
                    now = Time.GetUtcNow(),
                });
        }

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public Task<int> RunJobsAsync() => Get<JobRunner>().RunDueJobsAsync(CancellationToken.None);

    public async Task UploadAsync(IEnumerable<byte[]> chunks)
    {
        var client = CreateAuthorizedClient();
        foreach (var chunk in chunks)
        {
            (await client.PostAsync("/api/v1/chunks", TestChunks.Content(chunk))).EnsureSuccessStatusCode();
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = db.ConnectionString,
            ["Nytka:AdminToken"] = Token,
            ["Nytka:Stt:Url"] = "http://stt.test/v1/audio/transcriptions",
        };
        configure?.Invoke(settings);

        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        // ConfigureTestServices runs after Program.cs registered its services, so these win.
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(Time);

            var background = s.Where(d => d.ServiceType == typeof(IHostedService)
                    && (d.ImplementationType == typeof(JobRunnerService) || d.ImplementationType == typeof(SchedulerService)))
                .ToList();
            foreach (var descriptor in background)
            {
                s.Remove(descriptor);
            }

            // The last primary-handler registration wins: every test host talks to the fake.
            s.AddHttpClient<TranscriptionClient>().ConfigurePrimaryHttpMessageHandler(Stt.CreateHandler);

            s.AddSingleton<IVoiceActivityDetector, EnergyVad>();

            services?.Invoke(s);
        });
    }
}
