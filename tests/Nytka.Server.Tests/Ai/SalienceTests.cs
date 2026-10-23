using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Ai;

/// <summary>Salience on extracted items and media kept out of the prompt (second-brain redesign, step 2).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SalienceTests(PostgresFixture db) : AiTestBase(db)
{
    [Fact]
    public async Task Each_task_keeps_the_salience_the_model_gave_it_and_an_unknown_value_is_dropped()
    {
        Llm.Respond = _ => FakeLlm.AnswerItems(
            "Trip", "They planned a trip.",
            new FakeLlm.Item("Call Ben about the car", TaskKinds.Commitment, Salience: "vital"),
            new FakeLlm.Item("Buy milk", TaskKinds.Commitment, Salience: "urgent"),
            new FakeLlm.Item("Try the new coffee place", TaskKinds.Idea, Salience: "noise"));
        await Seed(Talk, Talk);

        await TickAndRun();

        var tasks = await Server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/tasks?kind=all");
        var salience = tasks.GetProperty("items").EnumerateArray().ToDictionary(
            t => t.GetProperty("text").GetString()!, t => t.GetProperty("salience").GetString());
        Assert.Equal("vital", salience["Call Ben about the car"]);
        Assert.Null(salience["Buy milk"]);
        Assert.Equal("noise", salience["Try the new coffee place"]);
    }

    [Fact]
    public void A_note_takes_the_highest_salience_of_its_tips()
    {
        var kept = ItemClassifier.Classify(
            [
                new AnswerItem("Keep a relaxed grip", TaskKinds.Advice, TaskKinds.Wearer, null, "table tennis", null, Salience.Ordinary),
                new AnswerItem("Watch the ball, not the bat", TaskKinds.Advice, TaskKinds.Wearer, null, "table tennis", null, Salience.Vital),
                new AnswerItem("Stretch first", TaskKinds.Advice, TaskKinds.Wearer, null, "warm up", null),
            ],
            _ => null);

        Assert.Equal([Salience.Vital, null], kept.Notes.Select(n => n.Salience));
    }

    [Fact]
    public async Task A_run_of_media_lines_reaches_the_model_as_one_line_without_its_words()
    {
        await Seed(Talk, "la la la the chorus of a song", "and the second verse of it", Talk);
        await Db.ExecuteAsync("update segments set speech_kind = 'media' where text like '%song%' or text like '%verse%'");

        await TickAndRun();

        var transcript = Assert.Single(Llm.Requests).User;
        Assert.DoesNotContain("chorus", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("verse", transcript, StringComparison.Ordinal);
        Assert.Single(transcript.Split('\n'), l => l.EndsWith($"Media: {EnrichConversationHandler.MediaPlaying}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_conversation_that_is_only_media_is_too_short_and_costs_no_call()
    {
        var id = await Seed(Talk, Talk);
        await Db.ExecuteAsync("update segments set speech_kind = 'media'");

        await TickAndRun();

        Assert.Empty(Llm.Requests);
        Assert.Equal(("skipped", EnrichConversationHandler.TooShort), ((await Ai(id)).AiStatus, (await Ai(id)).AiMessage));
    }

    [Fact]
    public async Task A_conversation_mostly_of_media_is_offered_no_tags()
    {
        await Seed(Talk, "a film plays", "a film plays", "a film plays", "a film plays", "a film plays");
        await Db.ExecuteAsync("update segments set ended_at = started_at + interval '10 seconds' where text = 'a film plays'");
        await Db.ExecuteAsync("update segments set speech_kind = 'media' where text = 'a film plays'");

        await TickAndRun();

        Assert.Contains("Tags: return an empty list.", Assert.Single(Llm.Requests).System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_fragment_is_listed_as_noise_and_the_list_can_hide_it()
    {
        var talk = await Seed(Talk);
        var fragment = await SeedAt(Now.AddMinutes(-30), "closed", "uh huh yes");
        var media = await SeedAt(Now.AddMinutes(-60), "closed", Talk);
        await Db.ExecuteAsync("update segments set speech_kind = 'media' where conversation_id = @media", new { media });
        var client = Server.CreateAuthorizedClient();

        var all = await client.GetFromJsonAsync<JsonElement>("/api/v1/conversations");
        var hidden = await client.GetFromJsonAsync<JsonElement>("/api/v1/conversations?noise=hide");
        var bad = await client.GetAsync("/api/v1/conversations?noise=maybe");

        Assert.Equal(
            new Dictionary<Guid, bool> { [talk] = false, [fragment] = true, [media] = true },
            all.GetProperty("items").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetGuid(), c => c.GetProperty("noise").GetBoolean()));
        Assert.Equal([talk], hidden.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
