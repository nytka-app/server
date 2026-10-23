using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Memories;

namespace Nytka.Server.Tests.Memories;

/// <summary>Facts that stop being true, and the salience of a fact (second-brain redesign, step 2).</summary>
[Collection(PostgresCollection.Name)]
public sealed class MemoryExpiryTests(PostgresFixture db) : MemoryTestBase(db)
{
    private static string Answer(params object[] memories) => JsonSerializer.Serialize(new { memories });

    private async Task<List<string>> Listed(string query = "")
    {
        var page = await Server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/memories" + query);
        return [.. page.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("text").GetString()!)];
    }

    [Fact]
    public async Task A_fact_with_an_end_date_leaves_the_list_after_that_day_and_stays_on_request()
    {
        Time.SetUtcNow(new DateTimeOffset(Start.AddHours(1)));
        Llm.Respond = _ => Answer(
            new { text = "You are staying in Lviv until Friday.", replaces = (string?)null, salience = "ordinary", expires = "2026-10-02" },
            new { text = "Your sister is called Olena.", replaces = (string?)null, salience = "vital", expires = (string?)null });

        await PublishReadyAsync(Conversation);
        await Server.RunJobsAsync();

        Assert.Equal(2, (await Listed()).Count);
        Assert.Equal(
            new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc),
            await Db.ScalarAsync<DateTime>("select expires_at from memories where text like '%Lviv%'"));
        Assert.Equal("vital", await Db.ScalarAsync<string>("select salience from memories where text like '%Olena%'"));

        Time.SetUtcNow(new DateTimeOffset(2026, 10, 3, 0, 0, 1, TimeSpan.Zero));

        Assert.Equal(["Your sister is called Olena."], await Listed());
        Assert.Equal(2, (await Listed("?expired=true")).Count);
    }

    [Theory]
    [InlineData("2026-09-28")]
    [InlineData("2028-10-01")]
    [InlineData("soon")]
    [InlineData(null)]
    public void A_date_before_the_conversation_far_ahead_or_not_a_date_is_no_expiry(string? text) =>
        Assert.Null(ExtractMemoriesHandler.ExpiresAt(text, new DateOnly(2026, 9, 29), TimeZoneInfo.Utc));

    [Fact]
    public void An_expiry_ends_with_the_owners_local_day() =>
        Assert.Equal(
            new DateTimeOffset(2026, 10, 2, 21, 0, 0, TimeSpan.Zero),
            ExtractMemoriesHandler.ExpiresAt("2026-10-02", new DateOnly(2026, 9, 29), TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")));
}
