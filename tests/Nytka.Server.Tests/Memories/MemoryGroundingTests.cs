using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Events;

namespace Nytka.Server.Tests.Memories;

/// <summary>A fact about "you" must cite a line of the wearer's (second-brain redesign, requirement 8).</summary>
[Collection(PostgresCollection.Name)]
public sealed class MemoryGroundingTests(PostgresFixture db) : MemoryTestBase(db)
{
    private static string Answer(params object[] memories) => JsonSerializer.Serialize(new { memories });

    private Task<long> Line(string text) => Db.ScalarAsync<long>("select id from segments where text = @text", new { text });

    private async Task<List<string>> Listed()
    {
        var page = await Server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/memories");
        return [.. page.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("text").GetString()!)];
    }

    [Fact]
    public async Task A_fact_that_cites_only_an_unlabelled_line_is_kept_unconfirmed_and_never_shown()
    {
        var unknown = await Line("I run every morning.");
        Llm.Respond = _ => Answer(new { text = "You run every morning.", replaces = (string?)null, salience = "ordinary", expires = (string?)null, segmentIds = new[] { unknown, 999999 } });

        await PublishReadyAsync(Conversation);
        await Server.RunJobsAsync();

        Assert.Empty(await Listed());
        Assert.False(await Db.ScalarAsync<bool>("select confirmed from memories"));
        Assert.Equal([unknown], await Db.QueryAsync<long>("select segment_id from memory_sources"));
        Assert.DoesNotContain(Recorded.Events, e => e.Type == NytkaEvent.MemoryCreated);
    }

    [Fact]
    public async Task A_fact_that_cites_a_line_of_the_wearer_is_shown_with_its_sources()
    {
        var mine = await Line("I run every morning.");
        await Db.ExecuteAsync("update segments set is_user = true where id = @mine", new { mine });
        var anna = await Line("My sister Olena lives in Lviv.");
        Llm.Respond = r =>
        {
            Assert.Contains($"#{mine} Wearer: I run every morning.", r.User, StringComparison.Ordinal);
            Assert.Contains($"#{anna} Anna:", r.User, StringComparison.Ordinal);
            return Answer(new { text = "You run every morning.", replaces = (string?)null, salience = "ordinary", expires = (string?)null, segmentIds = new[] { mine } });
        };

        await PublishReadyAsync(Conversation);
        await Server.RunJobsAsync();

        Assert.Equal(["You run every morning."], await Listed());
        Assert.Equal([mine], await Db.QueryAsync<long>("select segment_id from memory_sources"));
        Assert.Contains(Recorded.Events, e => e.Type == NytkaEvent.MemoryCreated);
    }

    [Fact]
    public async Task A_bookmark_within_a_minute_of_a_cited_line_makes_the_fact_vital()
    {
        var mine = await Line("I run every morning.");
        await Db.ExecuteAsync("update segments set is_user = true where id = @mine", new { mine });
        var at = await Db.ScalarAsync<DateTime>("select ended_at from segments where id = @mine", new { mine });
        Llm.Respond = _ => Answer(new { text = "You run every morning.", replaces = (string?)null, salience = "noise", expires = (string?)null, segmentIds = new[] { mine } });

        await PublishReadyAsync(Conversation);
        await Server.RunJobsAsync();
        await Server.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/bookmarks", new { id = Guid.NewGuid(), at = at.AddSeconds(59), source = "pendant" });

        var memory = (await Server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/memories")).GetProperty("items")[0];
        Assert.Equal("vital", memory.GetProperty("salience").GetString());
        Assert.Equal(at.AddSeconds(59), memory.GetProperty("markedAt").GetDateTime());
        Assert.Equal(mine, memory.GetProperty("markedSegmentId").GetInt64());
    }

    [Fact]
    public async Task An_unconfirmed_fact_never_rewrites_a_known_memory()
    {
        await Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, created_at, updated_at)
            values ('018f0000-0000-7000-8000-0000000000a1', 'You live in Kyiv.', 'you live in kyiv', 'ai', now(), now())
            """);
        var unknown = await Line("I run every morning.");
        Llm.Respond = _ => Answer(new { text = "You live in Lviv.", replaces = "018f0000-0000-7000-8000-0000000000a1", salience = "vital", expires = (string?)null, segmentIds = new[] { unknown } });

        await PublishReadyAsync(Conversation);
        await Server.RunJobsAsync();

        Assert.Equal(["You live in Kyiv."], await Listed());
        Assert.Equal(2, await Db.ScalarAsync<long>("select count(*) from memories"));
    }
}
