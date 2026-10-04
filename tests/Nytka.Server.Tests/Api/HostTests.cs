using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Audio.Voice;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public class HostTests(PostgresFixture db)
{
    [Fact]
    public async Task Healthz_needs_no_token()
    {
        using var factory = new NytkaApiFactory(db);

        var response = await factory.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_refuses_a_missing_token()
    {
        using var factory = new NytkaApiFactory(db);

        var response = await factory.CreateClient().GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Api_refuses_a_wrong_token()
    {
        using var factory = new NytkaApiFactory(db);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", NytkaApiFactory.Token + "x");

        var response = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Info_reports_api_version_1()
    {
        using var factory = new NytkaApiFactory(db);

        var info = await factory.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/info");

        Assert.Equal(1, info.GetProperty("apiVersion").GetInt32());
        Assert.False(string.IsNullOrEmpty(info.GetProperty("serverVersion").GetString()));
        Assert.Equal("admin", info.GetProperty("scope").GetString());
        // The build copies the speaker model beside the binaries when scripts/fetch-speaker-model.sh fetched it.
        Assert.Equal(
            File.Exists(SpeakerEmbedder.DefaultPath) ? ["offline-sync", "voice", "people"] : ["offline-sync", "people"],
            info.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    [Theory]
    [InlineData("Nytka:AdminToken", null, "Nytka__AdminToken")]
    [InlineData("Nytka:AdminToken", "too-short", "Nytka__AdminToken")]
    [InlineData("Nytka:Stt:Url", null, "Nytka__Stt__Url")]
    [InlineData("Nytka:Stt:Url", "not a url", "Nytka__Stt__Url")]
    public void Refuses_to_start_with_bad_settings(string key, string? value, string message)
    {
        using var factory = new NytkaApiFactory(db, settings =>
        {
            if (value is null)
            {
                settings.Remove(key);
            }
            else
            {
                settings[key] = value;
            }
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(message, error.ToString(), StringComparison.Ordinal);
    }
}
