using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Nytka.Server.Tests.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Speech;

/// <summary>
/// Kinds, marks and the rule (docs/specs/speech-kind.md, S-1): no classifier exists yet, so a test sets a guess and a score on
/// a segment itself, as the classifier will. Synthetic lines only.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SpeechKindTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private SpeechStore Store => Server.Get<SpeechStore>();

    private sealed record Stored(string? Manual, string? Kind, string? Guess);

    private sealed record Row(string Text, string? Guess, string? Kind);

    private sealed record Applied(string Mode, float Threshold);

    private Task<long> SegmentId(string text) => Db.ScalarAsync<long>("select id from segments where text = @text", new { text });

    private async Task<Stored> State(string text) => (await Db.QueryAsync<Stored>(
        "select speech_manual as Manual, speech_kind as Kind, speech_guess as Guess from segments where text = @text", new { text })).Single();

    private Task<List<Row>> Rows(Guid conversation) => Db.QueryAsync<Row>(
        "select text as Text, speech_guess as Guess, speech_kind as Kind from segments where conversation_id = @conversation order by id",
        new { conversation });

    private Task Guess(string text, string? guess, float? score) =>
        Db.ExecuteAsync("update segments set speech_guess = @guess, speech_score = @score where text = @text", new { text, guess, score });

    private async Task Change(string key, string value) =>
        (await Client.PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string> { [key] = value } })).EnsureSuccessStatusCode();

    private Task<HttpResponseMessage> Patch(long segment, object body) => Client.PatchAsJsonAsync($"/api/v1/segments/{segment}", body);

    private Task<HttpResponseMessage> Mark(Guid conversation, object body) => Client.PostAsJsonAsync($"/api/v1/conversations/{conversation}/speech", body);

    private static Task<HttpResponseMessage> PostRaw(HttpClient client, string path, string body) =>
        client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));

    private async Task<Guid> Person(string name) =>
        (await (await Client.PostAsJsonAsync("/api/v1/people", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    /// <summary>
    /// One conversation of six lines: three the wearer's (the provider's label, Nytka's verdict, the wearer's own mark) and three not
    /// (the provider's label, no label at all, the provider's wearer label overruled by the wearer's own mark).
    /// </summary>
    private async Task<Guid> Conversation()
    {
        var id = await Seed("mine", "theirs", "unlabelled", "by voice", "not mine after all", "mine by mark");
        await Db.ExecuteAsync("update segments set is_user = true where text in ('mine', 'not mine after all')");
        await Db.ExecuteAsync("update segments set is_user = false where text in ('theirs', 'by voice', 'mine by mark')");
        await Db.ExecuteAsync("update segments set voice_checked = true, voice_is_user = true where text = 'by voice'");
        await Db.ExecuteAsync("update segments set is_user_manual = false where text = 'not mine after all'");
        await Db.ExecuteAsync("update segments set is_user_manual = true where text = 'mine by mark'");
        return id;
    }

    [Theory]
    [InlineData(SpeechKinds.Off)]
    [InlineData(SpeechKinds.Shadow)]
    [InlineData(SpeechKinds.On)]
    public async Task A_mark_sets_the_kind_in_every_mode_and_survives_apply(string mode)
    {
        await Seed("a line");
        await Guess("a line", SpeechKinds.Person, 0.1f);
        await Store.ApplyAsync(mode, 0.8f, default);

        Assert.True(await Store.MarkAsync(await SegmentId("a line"), SpeechKinds.Media, default));

        Assert.Equal(new Stored("media", "media", "person"), await State("a line"));
        await Store.ApplyAsync(mode, 0.7f, default);
        Assert.Equal(new Stored("media", "media", "person"), await State("a line"));
        await Store.ApplyAsync(mode == SpeechKinds.On ? SpeechKinds.Shadow : SpeechKinds.On, 0.8f, default);
        Assert.Equal(new Stored("media", "media", "person"), await State("a line"));
    }

    [Theory]
    [InlineData(SpeechKinds.Off, null)]
    [InlineData(SpeechKinds.Shadow, null)]
    [InlineData(SpeechKinds.On, SpeechKinds.Media)]
    public async Task Clearing_a_mark_lets_the_guess_apply_in_on_and_nothing_otherwise(string mode, string? kind)
    {
        await Seed("a line");
        await Guess("a line", SpeechKinds.Media, 0.9f);
        await Store.ApplyAsync(mode, 0.8f, default);
        var segment = await SegmentId("a line");

        await Store.MarkAsync(segment, SpeechKinds.Person, default);
        Assert.Equal(new Stored("person", "person", "media"), await State("a line"));

        Assert.True(await Store.MarkAsync(segment, null, default));
        Assert.Equal(new Stored(null, kind, "media"), await State("a line"));
    }

    [Fact]
    public async Task Marking_a_segment_that_does_not_exist_is_false_and_changes_nothing()
    {
        await Seed("a line");

        Assert.False(await Store.MarkAsync(await SegmentId("a line") + 1000, SpeechKinds.Media, default));

        Assert.Equal(new Stored(null, null, null), await State("a line"));
    }

    [Fact]
    public async Task Apply_derives_guesses_from_scores_at_the_threshold_and_keeps_calls_and_the_wearer()
    {
        var id = await Seed("at the line", "just below", "unsure floor", "under the floor", "no score at all", "a call", "the wearer", "unguessed");
        await Guess("at the line", null, 0.8f);
        await Guess("just below", null, 0.79f);
        await Guess("unsure floor", null, 0.65f);
        await Guess("under the floor", null, 0.64f);
        await Guess("no score at all", null, 0f);
        await Guess("a call", SpeechKinds.Call, 0.95f);
        await Guess("the wearer", SpeechKinds.Person, null);
        await Db.ExecuteAsync("update segments set speech_signals = array['wearer'] where text = 'the wearer'");

        await Store.ApplyAsync(SpeechKinds.On, 0.8f, default);

        Assert.Equal(
            [
                new Row("at the line", "media", "media"),
                new Row("just below", "unsure", "person"),
                new Row("unsure floor", "unsure", "person"),
                new Row("under the floor", "person", "person"),
                new Row("no score at all", "person", "person"),
                new Row("a call", "call", "call"),
                new Row("the wearer", "person", "person"),
                new Row("unguessed", null, null),
            ],
            await Rows(id));

        // A lower threshold moves the lines up from the same scores: no audio or model is read.
        await Store.ApplyAsync(SpeechKinds.On, 0.7f, default);

        Assert.Equal(
            [
                new Row("at the line", "media", "media"),
                new Row("just below", "media", "media"),
                new Row("unsure floor", "unsure", "person"),
                new Row("under the floor", "unsure", "person"),
                new Row("no score at all", "person", "person"),
                new Row("a call", "call", "call"),
                new Row("the wearer", "person", "person"),
                new Row("unguessed", null, null),
            ],
            await Rows(id));
        Assert.Equal(["wearer"], await Db.ScalarAsync<string[]>("select speech_signals from segments where text = 'the wearer'"));
    }

    [Fact]
    public async Task In_shadow_the_guesses_follow_the_scores_and_no_kind_is_set_except_by_a_mark()
    {
        var id = await Seed("on the tv", "marked", "a call");
        await Guess("on the tv", null, 0.9f);
        await Guess("marked", null, 0.9f);
        await Guess("a call", SpeechKinds.Call, 0.95f);
        await Store.ApplyAsync(SpeechKinds.On, 0.8f, default);
        await Store.MarkAsync(await SegmentId("marked"), SpeechKinds.Person, default);

        await Store.ApplyAsync(SpeechKinds.Shadow, 0.5f, default);

        Assert.Equal([new Row("on the tv", "media", null), new Row("marked", "media", "person"), new Row("a call", "call", null)], await Rows(id));
    }

    [Fact]
    public async Task Apply_records_what_the_stored_guesses_follow()
    {
        Assert.False(await Store.NeedsApplyAsync(SpeechKinds.Shadow, 0.8f, default));
        Assert.True(await Store.NeedsApplyAsync(SpeechKinds.On, 0.8f, default));
        Assert.True(await Store.NeedsApplyAsync(SpeechKinds.Shadow, 0.7f, default));

        await Store.ApplyAsync(SpeechKinds.On, 0.7f, default);

        Assert.False(await Store.NeedsApplyAsync(SpeechKinds.On, 0.7f, default));
        Assert.True(await Store.NeedsApplyAsync(SpeechKinds.Shadow, 0.7f, default));
        Assert.Equal(1, await Db.ScalarAsync<long>("select count(*) from speech_state"));
        Assert.Equal(
            new Applied("on", 0.7f),
            (await Db.QueryAsync<Applied>("select applied_mode as Mode, applied_threshold as Threshold from speech_state")).Single());
    }

    [Fact]
    public async Task A_changed_setting_reaches_the_stored_scores_through_the_scheduler()
    {
        var id = await Seed("on the tv", "marked");
        await Guess("on the tv", null, 0.75f);
        await Guess("marked", null, 0.75f);
        await Store.MarkAsync(await SegmentId("marked"), SpeechKinds.Person, default);

        await Change("speech.mode", "on");
        await TickAndRun();
        Assert.Equal([new Row("on the tv", "unsure", "person"), new Row("marked", "unsure", "person")], await Rows(id));

        await Change("speech.mediaThreshold", "0.7");
        await TickAndRun();

        Assert.Equal([new Row("on the tv", "media", "media"), new Row("marked", "media", "person")], await Rows(id));
        Assert.Equal(0.7f, await Db.ScalarAsync<float>("select applied_threshold from speech_state"), 0.0001f);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs where kind = 'apply-speech'"));
        await Change("speech.mode", "off");
        await TickAndRun();
        Assert.Equal([new Row("on the tv", "media", null), new Row("marked", "media", "person")], await Rows(id));
    }

    [Fact]
    public async Task The_bulk_route_marks_every_line_that_is_not_the_wearers_and_counts_them()
    {
        var id = await Conversation();

        var response = await Mark(id, new { kind = "media" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["marked"], (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            [new Row("mine", null, null), new Row("theirs", null, "media"), new Row("unlabelled", null, "media"), new Row("by voice", null, null),
             new Row("not mine after all", null, "media"), new Row("mine by mark", null, null)],
            await Rows(id));
        Assert.Equal(3, await Db.ScalarAsync<long>("select count(*) from segments where speech_manual = 'media'"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from segments where speech_manual is not null and text in ('mine', 'by voice', 'mine by mark')"));
        Assert.Equal(3, (await (await Mark(id, new { kind = "media" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("marked").GetInt32());
    }

    [Fact]
    public async Task The_bulk_route_marks_a_call_and_null_clears_the_marks_it_made()
    {
        var id = await Conversation();
        await Mark(id, new { kind = "call" });
        Assert.Equal(3, await Db.ScalarAsync<long>("select count(*) from segments where speech_manual = 'call' and speech_kind = 'call'"));

        var cleared = await Mark(id, new { kind = (string?)null });

        Assert.Equal(3, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("marked").GetInt32());
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from segments where speech_manual is not null or speech_kind is not null"));
    }

    [Fact]
    public async Task The_bulk_route_marks_in_on_too_and_a_clear_gives_the_guess_back()
    {
        var id = await Seed("on the tv", "mine");
        await Db.ExecuteAsync("update segments set is_user = true where text = 'mine'");
        await Guess("on the tv", SpeechKinds.Media, 0.9f);
        await Guess("mine", SpeechKinds.Person, null);
        await Store.ApplyAsync(SpeechKinds.On, 0.8f, default);

        await Mark(id, new { kind = "person" });
        Assert.Equal([new Row("on the tv", "media", "person"), new Row("mine", "person", "person")], await Rows(id));

        await Mark(id, new { kind = (string?)null });
        Assert.Equal([new Row("on the tv", "media", "media"), new Row("mine", "person", "person")], await Rows(id));
    }

    [Fact]
    public async Task The_bulk_route_answers_404_for_an_unknown_conversation()
    {
        await Conversation();

        Assert.Equal(HttpStatusCode.NotFound, (await Mark(Guid.NewGuid(), new { kind = "media" })).StatusCode);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from segments where speech_manual is not null"));
    }

    [Theory]
    [InlineData("""{"kind":"unsure"}""")]
    [InlineData("""{"kind":"Media"}""")]
    [InlineData("""{"kind":3}""")]
    [InlineData("""{"kind":true}""")]
    [InlineData("""{"other":"media"}""")]
    [InlineData("""{}""")]
    [InlineData("""["media"]""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task The_bulk_route_answers_400_for_a_bad_kind_and_marks_nothing(string body)
    {
        var id = await Conversation();

        var response = await PostRaw(Client, $"/api/v1/conversations/{id}/speech", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["kind"], (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from segments where speech_manual is not null"));
    }

    [Fact]
    public async Task A_patch_marks_one_line_alone_or_with_the_other_marks_and_null_clears_it()
    {
        await Seed("hello", "world");
        var hello = await SegmentId("hello");
        var world = await SegmentId("world");
        var anna = await Person("Anna");

        var alone = await Patch(hello, new { speechKind = "media" });
        Assert.Equal(HttpStatusCode.OK, alone.StatusCode);
        var segment = await alone.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("hello", "media", true), (segment.GetProperty("text").GetString(), segment.GetProperty("speechKind").GetString(), segment.GetProperty("speechMarked").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, segment.GetProperty("speechGuess").ValueKind);

        var all = await (await Patch(world, new { isUser = false, personId = anna, speechKind = "call" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            (false, "Anna", "call", "manual"),
            (all.GetProperty("isUser").GetBoolean(), all.GetProperty("personName").GetString(), all.GetProperty("speechKind").GetString(), all.GetProperty("isUserSource").GetString()));

        var cleared = await (await Patch(hello, new { speechKind = (string?)null })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((JsonValueKind.Null, false), (cleared.GetProperty("speechKind").ValueKind, cleared.GetProperty("speechMarked").GetBoolean()));
        Assert.Equal(new Stored(null, null, null), await State("hello"));
    }

    [Theory]
    [InlineData(SpeechKinds.Shadow, SpeechKinds.Person, null)]
    [InlineData(SpeechKinds.Shadow, SpeechKinds.Media, null)]
    [InlineData(SpeechKinds.Shadow, SpeechKinds.Call, null)]
    [InlineData(SpeechKinds.On, SpeechKinds.Person, SpeechKinds.Person)]
    [InlineData(SpeechKinds.On, SpeechKinds.Media, SpeechKinds.Person)]
    [InlineData(SpeechKinds.On, SpeechKinds.Call, SpeechKinds.Person)]
    public async Task A_line_the_label_rule_took_for_the_wearer_takes_any_mark_and_a_clear_gives_the_guess_back(string mode, string kind, string? cleared)
    {
        await Seed("mine");
        await Db.ExecuteAsync("update segments set is_user = true where text = 'mine'");
        await Guess("mine", SpeechKinds.Person, null);
        await Store.ApplyAsync(mode, 0.8f, default);
        var mine = await SegmentId("mine");

        var response = await Patch(mine, new { speechKind = kind });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var segment = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            (kind, true, true, "provider"),
            (segment.GetProperty("speechKind").GetString(), segment.GetProperty("speechMarked").GetBoolean(), segment.GetProperty("isUser").GetBoolean(), segment.GetProperty("isUserSource").GetString()));
        Assert.Equal(new Stored(kind, kind, "person"), await State("mine"));

        Assert.Equal(HttpStatusCode.OK, (await Patch(mine, new { speechKind = (string?)null })).StatusCode);
        Assert.Equal(new Stored(null, cleared, "person"), await State("mine"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_wearers_own_mark_and_the_kind_apply_together_whichever_way_the_wearers_mark_goes(bool isUser)
    {
        await Seed("mine");
        await Db.ExecuteAsync("update segments set is_user = true where text = 'mine'");
        var mine = await SegmentId("mine");

        var response = await Patch(mine, new { isUser, speechKind = "media" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Stored("media", "media", null), await State("mine"));
        Assert.Equal(isUser, await Db.ScalarAsync<bool?>("select is_user_manual from segments where text = 'mine'"));
    }

    [Theory]
    [InlineData("""{"speechKind":"unsure"}""")]
    [InlineData("""{"speechKind":"Media"}""")]
    [InlineData("""{"speechKind":""}""")]
    [InlineData("""{"speechKind":5}""")]
    [InlineData("""{"speechKind":true}""")]
    [InlineData("""{"isUser":true,"speechKind":"loud"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"other":true}""")]
    public async Task A_bad_patch_is_a_400_and_changes_nothing(string body)
    {
        await Seed("hello");

        var response = await Client.PatchAsync($"/api/v1/segments/{await SegmentId("hello")}", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from segments where speech_manual is not null or is_user_manual is not null"));
    }

    [Fact]
    public async Task The_empty_patch_names_the_three_fields()
    {
        await Seed("hello");

        var response = await Client.PatchAsync($"/api/v1/segments/{await SegmentId("hello")}", new StringContent("{}", Encoding.UTF8, "application/json"));

        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("isUser")[0].GetString();
        Assert.Equal("Give isUser, personId, speechKind or any of them.", message);
    }

    [Theory]
    [InlineData("media")]
    [InlineData("person")]
    [InlineData(null)]
    public async Task A_patch_of_an_unknown_segment_is_a_404(string? kind)
    {
        await Seed("hello");

        Assert.Equal(HttpStatusCode.NotFound, (await Patch(await SegmentId("hello") + 1000, new { speechKind = kind })).StatusCode);
    }

    [Fact]
    public async Task A_read_token_gets_403_on_both_marking_routes_and_may_still_read()
    {
        var id = await Seed("hello");
        var segment = await SegmentId("hello");
        using var reader = Server.CreateClientWithScope("read");

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PatchAsJsonAsync($"/api/v1/segments/{segment}", new { speechKind = "media" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/v1/conversations/{id}/speech", new { kind = "media" })).StatusCode);

        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from segments where speech_manual is not null"));
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/conversations/{id}")).StatusCode);
    }

    [Fact]
    public async Task The_detail_shows_the_kind_the_guess_the_score_the_signals_and_whether_a_mark_decides()
    {
        var id = await Seed("on the tv", "marked call", "plain");
        await Guess("on the tv", SpeechKinds.Media, 0.9f);
        await Db.ExecuteAsync("update segments set speech_signals = array['far', 'turn'] where text = 'on the tv'");
        await Store.ApplyAsync(SpeechKinds.On, 0.8f, default);
        await Patch(await SegmentId("marked call"), new { speechKind = "call" });

        var detail = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}");

        var segments = detail.GetProperty("segments").EnumerateArray().ToList();
        Assert.All(
            segments,
            s => Assert.Equal(["speechKind", "speechGuess", "speechScore", "speechSignals", "speechMarked"], s.EnumerateObject().Select(p => p.Name).TakeLast(5)));
        var tv = segments[0];
        Assert.Equal(("media", "media", false), (tv.GetProperty("speechKind").GetString(), tv.GetProperty("speechGuess").GetString(), tv.GetProperty("speechMarked").GetBoolean()));
        Assert.Equal(0.9f, tv.GetProperty("speechScore").GetSingle());
        Assert.Equal(["far", "turn"], tv.GetProperty("speechSignals").EnumerateArray().Select(s => s.GetString()));
        var call = segments[1];
        Assert.Equal(("call", JsonValueKind.Null, JsonValueKind.Null, true), (call.GetProperty("speechKind").GetString(), call.GetProperty("speechGuess").ValueKind, call.GetProperty("speechScore").ValueKind, call.GetProperty("speechMarked").GetBoolean()));
        Assert.Empty(call.GetProperty("speechSignals").EnumerateArray());
        var plain = segments[2];
        Assert.Equal(
            (JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null, false),
            (plain.GetProperty("speechKind").ValueKind, plain.GetProperty("speechGuess").ValueKind, plain.GetProperty("speechScore").ValueKind, plain.GetProperty("speechMarked").GetBoolean()));
        Assert.Empty(plain.GetProperty("speechSignals").EnumerateArray());
    }

    private async Task<(Guid Mostly, Guid Little, Guid None)> MediaConversations()
    {
        var mostly = await SeedAt(Now.AddHours(-1), "closed", "tv talk", "a reply");
        var little = await SeedAt(Now.AddHours(-2), "closed", "a chat", "tv blip");
        var none = await SeedAt(Now.AddHours(-3), "closed", "a silent one");
        await Db.ExecuteAsync("update segments set ended_at = started_at + interval '8 seconds' where text in ('tv talk', 'a chat')");
        await Db.ExecuteAsync("update segments set ended_at = started_at + interval '2 seconds' where text in ('a reply', 'tv blip')");
        await Db.ExecuteAsync("update segments set speech_kind = 'media' where text in ('tv talk', 'tv blip')");
        return (mostly, little, none);
    }

    private async Task<List<(Guid Id, double Share)>> Listed(string query = "") =>
        (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations{query}")).GetProperty("items").EnumerateArray()
            .Select(i => (i.GetProperty("id").GetGuid(), i.GetProperty("mediaShare").GetDouble())).ToList();

    [Fact]
    public async Task The_list_carries_the_share_of_speech_time_that_is_media()
    {
        var (mostly, little, none) = await MediaConversations();

        var items = await Listed();

        Assert.Equal([mostly, little, none], items.Select(i => i.Id));
        Assert.Equal([0.8, 0.2, 0], items.Select(i => i.Share).ToArray());
    }

    [Fact]
    public async Task Media_hide_leaves_out_the_conversations_that_are_mostly_media_and_only_keeps_them()
    {
        var (mostly, little, none) = await MediaConversations();

        Assert.Equal([little, none], (await Listed("?media=hide")).Select(i => i.Id));
        Assert.Equal([mostly], (await Listed("?media=only")).Select(i => i.Id));
        Assert.Equal([little], (await Listed("?media=hide&limit=1")).Select(i => i.Id));
    }

    [Theory]
    [InlineData("?media=maybe")]
    [InlineData("?media=")]
    [InlineData("?media=HIDE")]
    public async Task Any_other_media_value_is_a_400(string query)
    {
        await MediaConversations();

        var response = await Client.GetAsync($"/api/v1/conversations{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["media"], (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task The_export_carries_the_kind_and_whether_it_was_marked()
    {
        await Seed("on the tv", "plain");
        await Guess("plain", SpeechKinds.Media, 0.9f);
        await Store.ApplyAsync(SpeechKinds.Shadow, 0.8f, default);
        await Patch(await SegmentId("on the tv"), new { speechKind = "media" });

        var body = await (await Client.GetAsync("/api/v1/export")).Content.ReadAsStringAsync();

        var conversation = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement)
            .Single(l => l.GetProperty("type").GetString() == "conversation");
        Assert.Equal(
            [("media", true), (null, false)],
            conversation.GetProperty("segments").EnumerateArray().Select(s => (s.GetProperty("speechKind").GetString(), s.GetProperty("speechMarked").GetBoolean())));
        Assert.Equal(1, JsonDocument.Parse(body.Split('\n')[0]).RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Mcp_labels_a_media_line_Media_and_a_call_line_with_its_label_and_call()
    {
        var id = await Seed("on the tv", "the other end", "a call", "plain");
        await Db.ExecuteAsync("update segments set speaker = 'Anna' where text in ('on the tv', 'the other end')");
        await Store.MarkAsync(await SegmentId("on the tv"), SpeechKinds.Media, default);
        await Store.MarkAsync(await SegmentId("the other end"), SpeechKinds.Call, default);
        await Store.MarkAsync(await SegmentId("a call"), SpeechKinds.Call, default);

        var lines = await Server.Get<McpQueries>().SegmentsAsync(id, 10_000, default);

        Assert.Equal(["Media", "Anna (call)", "(call)", null], lines.Select(l => l.Speaker));
    }

    [Fact]
    public async Task Mcp_reads_Media_for_a_media_line_even_when_it_is_labelled_the_wearer()
    {
        var id = await Seed("on the tv", "mine");
        await Db.ExecuteAsync("update segments set is_user = true where text = 'mine'");
        await Store.MarkAsync(await SegmentId("on the tv"), SpeechKinds.Media, default);
        await Store.MarkAsync(await SegmentId("mine"), SpeechKinds.Media, default);

        var lines = await Server.Get<McpQueries>().SegmentsAsync(id, 10_000, default);

        Assert.Equal(["Media", "Media"], lines.Select(l => l.Speaker));
    }
}
