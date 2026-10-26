using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Nytka.Server.Events;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.People;

/// <summary>docs/specs/people.md, Auto-applied names: a sure suggestion names its voice at once and can be undone.</summary>
public sealed class AutoNameTests(PostgresFixture db) : AiTestBase(db)
{
    protected override bool NameSuggestions => true;

    protected override bool AutoApplyNames => true;

    private HttpClient Client => Server.CreateAuthorizedClient();

    private static string Answer(params (string Voice, string Name, long Segment, double Confidence)[] suggestions) =>
        JsonSerializer.Serialize(new
        {
            suggestions = suggestions.Select(s => new { voice = s.Voice, name = s.Name, role = (string?)null, segmentId = s.Segment, confidence = s.Confidence }),
        });

    /// <summary>A long unlabelled line, voice "4" introducing itself, a labelled line with no voice id being thanked, and the wearer.</summary>
    private async Task<(Guid Id, long Voice, long Label)> Talked()
    {
        var id = await Seed(Talk);
        await AddSegment(id, "Hi, I'm Olena.", Now.AddMinutes(-4), "SPEAKER_4", "4");
        await AddSegment(id, "Thanks, Marko.", Now.AddMinutes(-3), "SPEAKER_9");
        await AddSegment(id, "Nice to meet you.", Now.AddMinutes(-2), "SPEAKER_0", "0", true);
        var ids = await Db.QueryAsync<long>("select id from segments where conversation_id = @id order by started_at", new { id });
        return (id, ids[1], ids[2]);
    }

    private async Task Suggest(Guid conversation)
    {
        var source = Server.Get<NpgsqlDataSource>();
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Server.Get<IEventPublisher>().PublishAsync(new NytkaEvent(NytkaEvent.ConversationReady, conversation), connection, transaction, default);
            await transaction.CommitAsync();
        }

        await Server.RunJobsAsync();
    }

    private async Task<JsonElement[]> Listed(string status) =>
        (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/people/suggestions?status={status}")).GetProperty("items").EnumerateArray().ToArray();

    private Task<string> Status(string name) => Db.ScalarAsync<string>("select status from name_suggestions where name = @name", new { name });

    private Task<long> Count(string sql) => Db.ScalarAsync<long>(sql);

    private Task<HttpResponseMessage> Undo(Guid id) => Client.PostAsync($"/api/v1/people/suggestions/{id}/undo", null);

    [Fact]
    public async Task A_sure_self_introduction_names_the_voice_at_once_and_is_listed_as_auto_applied()
    {
        var (id, voice, _) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));

        await Suggest(id);

        var item = Assert.Single(await Listed("auto_applied"));
        Assert.Equal(("Olena", "speaker"), (item.GetProperty("name").GetString(), item.GetProperty("target").GetString()));
        Assert.Equal(Now.UtcDateTime, item.GetProperty("decidedAt").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(1));
        Assert.Empty(await Listed("pending"));
        Assert.Empty(await Listed("accepted"));
        Assert.Equal("Olena", await Db.ScalarAsync<string>("select p.name from person_voices v join people p on p.id = v.person_id where v.speaker_id = '4'"));
        var review = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/review")).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(review, i => i.GetProperty("kind").GetString() == "name");
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/v1/people/suggestions/{item.GetProperty("id").GetGuid()}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task A_suggestion_under_the_threshold_or_with_auto_apply_off_stays_pending()
    {
        var (id, voice, label) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.84), ("Voice B", "Marko", label, 0.95));
        StartServer(settings => settings["Nytka:People:AutoApplyNames"] = "false");
        await Suggest(id);
        Assert.Equal(2, (await Listed("pending")).Length);

        await Db.ExecuteAsync("delete from name_suggestions; delete from people_runs");
        StartServer();
        await Suggest(id);

        Assert.Equal("pending", await Status("Olena"));
        Assert.Equal("auto_applied", await Status("Marko"));
    }

    [Fact]
    public async Task The_threshold_is_a_setting()
    {
        var (id, voice, _) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));
        StartServer(settings => settings["Nytka:People:AutoApplyConfidence"] = "0.95");

        await Suggest(id);

        Assert.Equal("pending", await Status("Olena"));
    }

    [Fact]
    public async Task Undo_takes_back_an_addressed_name_and_the_person_it_made_and_it_is_never_applied_again()
    {
        var (id, _, label) = await Talked();
        Llm.Respond = _ => Answer(("Voice B", "Marko", label, 0.95));
        await Suggest(id);
        var suggestion = Assert.Single(await Listed("auto_applied")).GetProperty("id").GetGuid();
        Assert.Equal(1, await Count("select count(*) from segments where person_id is not null"));

        Assert.Equal(HttpStatusCode.NoContent, (await Undo(suggestion)).StatusCode);

        Assert.Equal(0, await Count("select count(*) from segments where person_id is not null"));
        Assert.Equal(0, await Count("select count(*) from people"));
        Assert.Equal("rejected", await Status("Marko"));
        Assert.Equal(HttpStatusCode.Conflict, (await Undo(suggestion)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Undo(Guid.NewGuid())).StatusCode);

        await Db.ExecuteAsync("delete from people_runs");
        await Suggest(id);

        Assert.Equal(1, await Count("select count(*) from name_suggestions"));
        Assert.Equal(0, await Count("select count(*) from people"));
    }

    [Fact]
    public async Task Undo_keeps_a_person_who_was_there_before_and_takes_only_the_voice_and_the_role_tag()
    {
        var (id, voice, _) = await Talked();
        var olena = Guid.NewGuid();
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@olena, 'Olena', now())", new { olena });
        await Db.ExecuteAsync("update segments set text = 'Hi, I''m Olena, the nurse.' where id = @voice", new { voice });
        Llm.Respond = _ => JsonSerializer.Serialize(new { suggestions = new[] { new { voice = "Voice A", name = "Olena", role = "nurse", segmentId = voice, confidence = 0.9 } } });
        await Suggest(id);
        Assert.Equal(1, await Count("select count(*) from person_tags"));
        var suggestion = Assert.Single(await Listed("auto_applied")).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await Undo(suggestion)).StatusCode);

        Assert.Equal(olena, await Db.ScalarAsync<Guid>("select id from people"));
        Assert.Equal(0, await Count("select count(*) from person_voices"));
        Assert.Equal(0, await Count("select count(*) from person_tags"));
    }

    [Fact]
    public async Task Undo_gives_a_person_known_by_role_their_role_name_back()
    {
        var repairman = Guid.NewGuid();
        await Db.ExecuteAsync(
            "insert into people (id, name, created_at, named) values (@repairman, 'Repairman', now(), false); insert into person_voices (speaker_id, person_id, created_at) values ('4', @repairman, now())",
            new { repairman });
        var id = await Seed(Talk);
        await AddSegment(id, "Thanks for waiting.", Now.AddMinutes(-4), "SPEAKER_4", "4");
        await AddSegment(id, "Thanks, Mykola.", Now.AddMinutes(-3), "SPEAKER_0", "0", true);
        var wearer = await MaxSegmentId(id);
        Llm.Respond = _ => Answer(("Voice A", "Mykola", wearer, 0.9));
        await Suggest(id);
        Assert.Equal(("Mykola", true), (await Db.QueryAsync<(string, bool)>("select name, named from people")).Single());

        Assert.Equal(HttpStatusCode.NoContent, (await Undo(Assert.Single(await Listed("auto_applied")).GetProperty("id").GetGuid())).StatusCode);

        Assert.Equal(("Repairman", false), (await Db.QueryAsync<(string, bool)>("select name, named from people")).Single());
        Assert.Equal(repairman, await Db.ScalarAsync<Guid>("select person_id from person_voices where speaker_id = '4'"));
    }

    [Fact]
    public async Task One_name_for_two_voices_waits_for_the_owner()
    {
        var (id, voice, label) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9), ("Voice B", "Olena", label, 0.9));

        await Suggest(id);

        Assert.Equal(0, await Count("select count(*) from name_suggestions where status <> 'pending'"));
        Assert.Equal(0, await Count("select count(*) from people"));
    }

    [Fact]
    public async Task A_second_name_for_a_voice_that_already_has_one_pending_waits_for_the_owner()
    {
        var (id, voice, _) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.6));
        await Suggest(id);
        await AddSegment(id, "Call me Olga, actually.", Now.AddMinutes(-1), "SPEAKER_4", "4");
        var later = await MaxSegmentId(id);
        Llm.Respond = _ => Answer(("Voice A", "Olga", later, 0.95));

        await Suggest(id);

        Assert.Equal("pending", await Status("Olga"));
        Assert.Equal(0, await Count("select count(*) from people"));
    }

    [Fact]
    public async Task A_name_a_person_known_by_role_has_waits_for_the_owner()
    {
        var (id, voice, _) = await Talked();
        await Db.ExecuteAsync("insert into people (id, name, created_at, named) values (gen_random_uuid(), 'Olena', now(), false)");
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));

        await Suggest(id);

        Assert.Equal("pending", await Status("Olena"));
        Assert.Equal(0, await Count("select count(*) from person_voices"));
    }
}
