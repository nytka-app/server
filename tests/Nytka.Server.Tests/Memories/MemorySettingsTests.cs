using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Memories;
using Nytka.Server.Settings;

namespace Nytka.Server.Tests.Memories;

[Collection(PostgresCollection.Name)]
public sealed class MemorySettingsTests(PostgresFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_catalog_lists_both_keys_with_their_defaults()
    {
        using var server = new NytkaApiFactory(db);

        var items = (await server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/settings")).GetProperty("items");

        var enabled = items.EnumerateArray().Single(i => i.GetProperty("key").GetString() == "memories.enabled");
        Assert.Equal("bool", enabled.GetProperty("type").GetString());
        Assert.Equal("true", enabled.GetProperty("value").GetString());
        var name = items.EnumerateArray().Single(i => i.GetProperty("key").GetString() == "memories.userName");
        Assert.Equal("string", name.GetProperty("type").GetString());
        Assert.False(name.GetProperty("isSet").GetBoolean());
    }

    [Fact]
    public async Task They_resolve_from_the_environment_and_the_table_and_reject_bad_values()
    {
        using var environment = new NytkaApiFactory(db, s => s["Nytka:Memories:Enabled"] = "false");
        using var table = new NytkaApiFactory(db);
        var patch = await table.CreateAuthorizedClient().PatchAsync(
            "/api/v1/settings", JsonContent.Create(new { values = new Dictionary<string, string> { ["memories.userName"] = "Yehor" } }));
        var bad = await table.CreateAuthorizedClient().PatchAsync(
            "/api/v1/settings", JsonContent.Create(new { values = new Dictionary<string, string> { ["memories.enabled"] = "maybe", ["memories.userName"] = new string('x', 65) } }));

        Assert.False(MemorySettings.IsEnabled(environment.Get<SettingsService>()));
        Assert.True(MemorySettings.IsEnabled(table.Get<SettingsService>()));
        Assert.Equal(System.Net.HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("Yehor", MemorySettings.UserName(table.Get<SettingsService>()));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal(2, errors.EnumerateObject().Count());
    }

    [Fact]
    public async Task The_environment_locks_a_key()
    {
        using var server = new NytkaApiFactory(db, s => s["Nytka:Memories:Enabled"] = "false");

        var response = await server.CreateAuthorizedClient().PatchAsync(
            "/api/v1/settings", JsonContent.Create(new { values = new Dictionary<string, string> { ["memories.enabled"] = "true" } }));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
    }
}
