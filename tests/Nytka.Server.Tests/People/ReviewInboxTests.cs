using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Storage;

namespace Nytka.Server.Tests.People;

/// <summary>The review inbox (docs/specs/people.md, Review inbox): rows go in with SQL, answers come through the API.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReviewInboxTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = Server();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private NytkaApiFactory Server(bool matching = true) => new(
        db,
        settings =>
        {
            if (matching)
            {
                settings["Nytka:People:VoiceMatching"] = "true";
            }
        });

    private HttpClient Client => _server.CreateAuthorizedClient();

    private async Task<(Guid Conversation, long Batch)> Conversation(string? title = "A talk")
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, title, created_at, updated_at) values (@id, @Now, @Now, 'closed', @title, @Now, @Now)",
            new { id, Now, title });
        var batch = await db.ScalarAsync<long>(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@id, @Now, @Now, 'done', '{}', @Now) returning id",
            new { id, Now });
        return (id, batch);
    }

    private Task<long> Segment((Guid Conversation, long Batch) c, string text, DateTimeOffset at, float? similarity = null, bool? verdict = null, bool? manual = null) =>
        db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, voice_checked, voice_similarity, voice_is_user, is_user_manual)
            values (@Conversation, @Batch, @at, @at, @text, @checked, @similarity, @verdict, @manual) returning id
            """,
            new { c.Conversation, c.Batch, at, text, @checked = similarity is not null, similarity, verdict, manual });

    private Task<Guid> Person(string name) => _server.Get<PeopleStore>().CreateAsync(name, Now, default);

    private async Task<Guid> Suggestion(Guid conversation, long evidence, string name, DateTimeOffset at, string speakerId = "4")
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            """
            insert into name_suggestions (id, conversation_id, target, speaker_id, name, evidence_segment_id, confidence, status, created_at)
            values (@id, @conversation, 'speaker', @speakerId, @name, @evidence, 0.9, 'pending', @at)
            """,
            new { id, conversation, speakerId, name, evidence, at });
        return id;
    }

    private async Task<Guid> Match(Guid conversation, Guid person, long[] segments, DateTimeOffset at)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into voice_matches (id, conversation_id, person_id, segment_ids, similarity, status, created_at) values (@id, @conversation, @person, @segments, 0.71, 'pending', @at)",
            new { id, conversation, person, segments, at });
        return id;
    }

    private async Task<JsonElement[]> Inbox(string query = "") =>
        (await Client.GetFromJsonAsync<JsonElement>("/api/v1/review" + query)).GetProperty("items").EnumerateArray().ToArray();

    private static string[] Kinds(IEnumerable<JsonElement> items) => [.. items.Select(i => i.GetProperty("kind").GetString()!)];

    private Task<HttpResponseMessage> Answer(string kind, object id, string verb) =>
        Client.PostAsync($"/api/v1/review/{kind}/{id}/{verb}", null);

    private Task<bool?> Manual(long segment) =>
        db.ScalarAsync<bool?>("select is_user_manual from segments where id = @segment", new { segment });

    /// <summary>One of each kind: a name 3 min ago, a match 2 min ago and a label 1 min ago (its similarity 0.40 is within 0.05 of 0.38).</summary>
    private async Task<(Guid Name, Guid Match, long Label, long Hello)> Seeded()
    {
        var c = await Conversation();
        var hello = await Segment(c, "Hi, I'm Olena.", Now.AddMinutes(-4));
        var other = await Segment(c, "Right.", Now.AddMinutes(-5));
        var label = await Segment(c, "Yes, that works.", Now.AddMinutes(-1), 0.40f, true);
        var name = await Suggestion(c.Conversation, hello, "Olena", Now.AddMinutes(-3));
        var match = await Match(c.Conversation, await Person("Marko"), [other], Now.AddMinutes(-2));
        return (name, match, label, hello);
    }

    [Fact]
    public async Task Each_kind_is_listed_newest_first_with_its_proposal()
    {
        await Seeded();

        var items = await Inbox();

        Assert.Equal(["label", "voice", "name"], Kinds(items));
        var label = items[0];
        Assert.Equal("Yes, that works.", label.GetProperty("text").GetString());
        Assert.Equal("A talk", label.GetProperty("conversationTitle").GetString());
        Assert.True(label.GetProperty("proposal").GetProperty("isUser").GetBoolean());
        Assert.Equal(0.40, label.GetProperty("proposal").GetProperty("similarity").GetDouble(), 2);
        var voice = items[1].GetProperty("proposal");
        Assert.Equal("Marko", voice.GetProperty("name").GetString());
        Assert.Equal(0.71, voice.GetProperty("similarity").GetDouble(), 2);
        Assert.Equal("Right.", items[1].GetProperty("text").GetString());
        var name = items[2].GetProperty("proposal");
        Assert.Equal("Olena", name.GetProperty("name").GetString());
        Assert.Equal(0.9, name.GetProperty("confidence").GetDouble(), 2);
        Assert.Equal("Hi, I'm Olena.", items[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_conversation_without_a_manual_title_shows_its_AI_title_in_every_kind()
    {
        await Seeded();
        await db.ExecuteAsync("update conversations set title = null, ai_title = 'Chat with Olena'");

        var items = await Inbox();

        Assert.Equal(3, items.Length);
        Assert.All(items, i => Assert.Equal("Chat with Olena", i.GetProperty("conversationTitle").GetString()));
    }

    [Fact]
    public async Task A_manual_title_wins_over_the_AI_title()
    {
        await Seeded();
        await db.ExecuteAsync("update conversations set ai_title = 'Chat with Olena'");

        var items = await Inbox();

        Assert.All(items, i => Assert.Equal("A talk", i.GetProperty("conversationTitle").GetString()));
    }

    [Fact]
    public async Task A_label_is_offered_only_near_the_threshold_without_a_mark_and_within_14_days()
    {
        var c = await Conversation();
        await Segment(c, "near", Now.AddDays(-1), 0.34f, false);
        await Segment(c, "far", Now.AddDays(-1), 0.60f, true);
        await Segment(c, "marked", Now.AddDays(-1), 0.40f, true, manual: true);
        await Segment(c, "old", Now.AddDays(-15), 0.40f, true);
        await Segment(c, "unchecked", Now.AddDays(-1));

        var items = await Inbox();

        Assert.Equal("near", Assert.Single(items).GetProperty("text").GetString());
    }

    [Fact]
    public async Task At_most_20_labels_are_offered()
    {
        var c = await Conversation();
        for (var i = 0; i < 25; i++)
        {
            await Segment(c, $"line {i}", Now.AddMinutes(-i), 0.38f, true);
        }

        Assert.Equal(20, (await Inbox("?limit=200")).Length);
    }

    [Fact]
    public async Task Limit_caps_the_merged_list_to_the_newest()
    {
        await Seeded();

        var items = await Inbox("?limit=2");

        Assert.Equal(["label", "voice"], Kinds(items));
    }

    [Fact]
    public async Task With_voice_matching_off_there_are_no_voice_items()
    {
        _server.Dispose();
        _server = Server(matching: false);
        await Seeded();

        Assert.Equal(["label", "name"], Kinds(await Inbox()));
    }

    [Fact]
    public async Task Accepting_a_name_names_the_voice_and_it_leaves_the_inbox()
    {
        var (name, _, _, _) = await Seeded();

        var response = await Answer("name", name, "accept");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Olena", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.DoesNotContain("name", Kinds(await Inbox()));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from person_voices where speaker_id = '4'"));
        Assert.Equal(HttpStatusCode.Conflict, (await Answer("name", name, "accept")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Answer("name", name, "reject")).StatusCode);
    }

    [Fact]
    public async Task Rejecting_a_name_leaves_the_inbox_and_names_nothing()
    {
        var (name, _, _, _) = await Seeded();

        Assert.Equal(HttpStatusCode.NoContent, (await Answer("name", name, "reject")).StatusCode);

        Assert.DoesNotContain("name", Kinds(await Inbox()));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from person_voices"));
        Assert.Equal("rejected", await db.ScalarAsync<string>("select status from name_suggestions"));
    }

    [Fact]
    public async Task Accepting_a_match_links_its_segments_and_it_leaves_the_inbox()
    {
        var (_, match, _, _) = await Seeded();
        var segment = await db.ScalarAsync<long>("select segment_ids[1] from voice_matches");

        var response = await Answer("voice", match, "accept");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Marko", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.DoesNotContain("voice", Kinds(await Inbox()));
        Assert.NotNull(await db.ScalarAsync<Guid?>("select person_id from segments where id = @segment", new { segment }));
        Assert.Equal(HttpStatusCode.NotFound, (await Answer("voice", match, "accept")).StatusCode);
    }

    [Fact]
    public async Task Rejecting_a_match_leaves_the_inbox_and_links_nothing()
    {
        var (_, match, _, _) = await Seeded();

        Assert.Equal(HttpStatusCode.NoContent, (await Answer("voice", match, "reject")).StatusCode);

        Assert.DoesNotContain("voice", Kinds(await Inbox()));
        Assert.Equal("rejected", await db.ScalarAsync<string>("select status from voice_matches"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from segments where person_id is not null"));
    }

    [Fact]
    public async Task Accepting_a_label_stores_the_verdict_as_the_wearers_mark_and_rejecting_the_opposite()
    {
        var c = await Conversation();
        var yes = await Segment(c, "says yes", Now.AddMinutes(-1), 0.40f, true);
        var no = await Segment(c, "says no", Now.AddMinutes(-2), 0.36f, false);
        var rejectedYes = await Segment(c, "reject yes", Now.AddMinutes(-3), 0.40f, true);
        var rejectedNo = await Segment(c, "reject no", Now.AddMinutes(-4), 0.36f, false);

        Assert.Equal(HttpStatusCode.NoContent, (await Answer("label", yes, "accept")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Answer("label", no, "accept")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Answer("label", rejectedYes, "reject")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Answer("label", rejectedNo, "reject")).StatusCode);

        Assert.True(await Manual(yes));
        Assert.False(await Manual(no));
        Assert.False(await Manual(rejectedYes));
        Assert.True(await Manual(rejectedNo));
        Assert.Empty(await Inbox());
        Assert.Equal(HttpStatusCode.NotFound, (await Answer("label", yes, "accept")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_kind_or_item_is_404_and_answers_need_an_admin_token()
    {
        var (name, _, _, _) = await Seeded();

        Assert.Equal(HttpStatusCode.NotFound, (await Answer("tag", name, "accept")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Answer("name", Guid.NewGuid(), "accept")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Answer("label", "x", "reject")).StatusCode);
        var reader = _server.CreateClientWithScope("read");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/review")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync($"/api/v1/review/name/{name}/accept", null)).StatusCode);
    }
}
