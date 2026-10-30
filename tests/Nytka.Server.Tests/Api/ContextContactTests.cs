using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Nytka.Server.Tests.Api;

/// <summary>The optional <c>contact</c> of a call range (nytka-app/server#302): a display name and a hash of the number, on a call only.</summary>
public sealed class ContextContactTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private static readonly string Hash = new('a', 64);

    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = new NytkaApiFactory(db);
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private async Task<JsonElement> Post(string kind, Dictionary<string, object?> extra)
    {
        var item = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid(), ["kind"] = kind, ["route"] = "speaker",
            ["startedAt"] = "2026-09-29T09:00:00+00:00", ["endedAt"] = "2026-09-29T09:05:00+00:00",
        };
        foreach (var pair in extra) { item[pair.Key] = pair.Value; }
        var response = await _server.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/context/ranges", new { items = new[] { item } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_call_keeps_a_trimmed_contact_and_a_lower_case_hash_and_the_list_returns_them()
    {
        var result = await Post("call", new() { ["contact"] = "  Olena K.  ", ["contactHash"] = Hash.ToUpperInvariant() });

        Assert.Equal(1, result.GetProperty("accepted").GetInt32());
        var listed = (await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/context/ranges")).GetProperty("items")[0];
        Assert.Equal("Olena K.", listed.GetProperty("contact").GetString());
        Assert.Equal(Hash, listed.GetProperty("contactHash").GetString());
    }

    [Fact]
    public async Task A_call_needs_no_contact()
    {
        Assert.Equal(1, (await Post("call", [])).GetProperty("accepted").GetInt32());
        Assert.Equal(1, (await Post("call", new() { ["contact"] = null })).GetProperty("accepted").GetInt32());
    }

    [Theory]
    [InlineData("media", "Olena K.", null)]
    [InlineData("call", "", null)]
    [InlineData("call", "   ", null)]
    [InlineData("call", "Olena\nK.", null)]
    [InlineData("call", null, "aaaa")]
    [InlineData("call", "Olena K.", "not-hex")]
    public async Task A_contact_on_another_kind_a_bad_name_or_a_bad_hash_refuses_that_row_only(string kind, string? contact, string? hash)
    {
        var extra = new Dictionary<string, object?>();
        if (contact is not null) { extra["contact"] = contact; }
        if (hash is not null) { extra["contactHash"] = hash; }

        var result = await Post(kind, extra);

        Assert.Equal(0, result.GetProperty("accepted").GetInt32());
        Assert.Equal("bad-contact", result.GetProperty("rejected")[0].GetProperty("reason").GetString());
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from context_ranges"));
    }

    [Fact]
    public async Task A_name_over_80_characters_is_refused()
    {
        var result = await Post("call", new() { ["contact"] = new string('x', 81) });

        Assert.Equal("bad-contact", result.GetProperty("rejected")[0].GetProperty("reason").GetString());
    }
}
