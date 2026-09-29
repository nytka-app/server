using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Nytka.Server.Tests;

/// <summary>The real server on a real (Testcontainers) database, with a fake clock.</summary>
public sealed class NytkaApiFactory(PostgresFixture db, Action<IDictionary<string, string?>>? configure = null)
    : WebApplicationFactory<Program>
{
    public const string Token = "test-admin-token-0123456789-abcdefghij";

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
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

        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Time));
    }
}
