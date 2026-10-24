using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nytka.Server.Auth;
using Nytka.Server.Privacy;
using Nytka.Server.Settings;
using Nytka.Server.Tests.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Privacy;

/// <summary>Sensitive content (docs/explanation/features.md, Sensitive content): classified with the summary, hidden from untrusted readers.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SensitivityTests(PostgresFixture db) : AiTestBase(db)
{
    private static string Answer(string title, string sensitivity, string taskSensitivity) => JsonSerializer.Serialize(new
    {
        title, summary = "s", tags = Array.Empty<string>(), sensitivity,
        items = new[]
        {
            new { text = "Book the next session", kind = "commitment", owner = "wearer", person = (string?)null, topic = (string?)null, due = (string?)null, salience = "ordinary", segmentIds = Array.Empty<long>(), sensitivity = taskSensitivity },
        },
    });

    /// <summary>A therapy conversation and an ordinary one, both summarized.</summary>
    private async Task<(Guid Therapy, Guid Plain)> SeedTwo()
    {
        var therapy = await Seed(Talk, Talk);
        var plain = await SeedAt(Now.AddHours(-2), "closed", Talk, Talk);
        Llm.Respond = r => r.User.Contains(Now.AddMinutes(-6).ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            ? Answer("Session", "therapy", "therapy")
            : Answer("Lunch", "none", "none");
        await TickAndRun();
        return (therapy, plain);
    }

    private static async Task<List<Guid>> Listed(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/api/v1/conversations")).GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid())];

    [Fact]
    public async Task The_summary_classifies_the_conversation_and_its_items()
    {
        var (therapy, plain) = await SeedTwo();

        Assert.Equal("therapy", await Db.ScalarAsync<string>("select sensitivity from conversations where id = @therapy", new { therapy }));
        Assert.Equal("none", await Db.ScalarAsync<string>("select sensitivity from conversations where id = @plain", new { plain }));
        Assert.Equal("therapy", await Db.ScalarAsync<string>("select sensitivity from tasks where conversation_id = @therapy", new { therapy }));
    }

    [Fact]
    public async Task A_read_token_never_sees_a_hidden_conversation_and_admin_and_a_sensitive_token_do()
    {
        var (therapy, plain) = await SeedTwo();
        var read = Server.CreateClientWithScope("read");

        Assert.Equal([plain], await Listed(read));
        Assert.Equal(HttpStatusCode.NotFound, (await read.GetAsync($"/api/v1/conversations/{therapy}")).StatusCode);
        Assert.DoesNotContain("Book the next session", await read.GetStringAsync("/api/v1/tasks"), StringComparison.Ordinal);
        Assert.Equal(2, (await Listed(Server.CreateAuthorizedClient())).Count);
        Assert.Equal(2, (await Listed(Server.CreateClientWithScope("read", sensitive: true))).Count);
        var detail = await Server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{therapy}");
        Assert.Equal("therapy", detail.GetProperty("sensitivity").GetString());
    }

    [Fact]
    public async Task The_owner_decides_what_is_hidden()
    {
        await SeedTwo();
        (await Server.CreateAuthorizedClient().PatchAsJsonAsync(
            "/api/v1/settings", new { values = new Dictionary<string, string> { [PrivacySettings.HideKey] = "none" } })).EnsureSuccessStatusCode();

        Assert.Equal(2, (await Listed(Server.CreateClientWithScope("read"))).Count);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Server.CreateAuthorizedClient().PatchAsJsonAsync(
                "/api/v1/settings", new { values = new Dictionary<string, string> { [PrivacySettings.HideKey] = "therapy,gossip" } })).StatusCode);
    }

    [Fact]
    public void Mcp_hides_for_admin_too_unless_the_token_is_sensitive()
    {
        var settings = Server.Get<SettingsService>();
        HttpContext Context(string path, string scope, bool sensitive)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            System.Security.Claims.Claim[] claims = sensitive
                ? [new(NytkaAuthenticationHandler.ScopeClaim, scope), new(NytkaAuthenticationHandler.SensitiveClaim, "true")]
                : [new(NytkaAuthenticationHandler.ScopeClaim, scope)];
            context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test"));
            return context;
        }

        string[] Hidden(string path, string scope, bool sensitive = false) =>
            new SensitivityScope(new HttpContextAccessor { HttpContext = Context(path, scope, sensitive) }, settings).Hidden;

        Assert.Empty(Hidden("/api/v1/conversations", NytkaScopes.Admin));
        Assert.Equal(Sensitivity.Classes, Hidden("/mcp", NytkaScopes.Admin));
        Assert.Equal(Sensitivity.Classes, Hidden("/api/v1/conversations", NytkaScopes.Read));
        Assert.Empty(Hidden("/mcp", NytkaScopes.Read, sensitive: true));
    }

    [Fact]
    public async Task A_webhook_gets_a_hidden_conversation_only_when_it_was_made_sensitive()
    {
        var admin = Server.CreateAuthorizedClient();
        (await admin.PostAsJsonAsync("/api/v1/webhooks", new { url = "https://plain.example.test/hook", events = new[] { "conversation.ready" } })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/v1/webhooks", new { url = "https://trusted.example.test/hook", events = new[] { "conversation.ready" }, sensitive = true })).EnsureSuccessStatusCode();

        await SeedTwo();

        Assert.Equal(
            ["https://plain.example.test/hook", "https://trusted.example.test/hook", "https://trusted.example.test/hook"],
            await Db.QueryAsync<string>("select w.url from webhook_deliveries d join webhooks w on w.id = d.webhook_id order by w.url"));
    }

    [Fact]
    public async Task An_export_leaves_hidden_content_out_unless_the_token_is_trusted()
    {
        await SeedTwo();
        var pairedApp = Server.CreateClientWithScope("admin");

        var partial = await pairedApp.GetStringAsync("/api/v1/export");
        var full = await Server.CreateAuthorizedClient().GetStringAsync("/api/v1/export");

        Assert.DoesNotContain("\"Session\"", partial, StringComparison.Ordinal);
        Assert.Contains("\"Lunch\"", partial, StringComparison.Ordinal);
        Assert.Contains("\"Session\"", full, StringComparison.Ordinal);
    }
}
