using Nytka.Server.Jobs;
using Nytka.Server.Tags;
using Nytka.Server.Tests.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Tags;

/// <summary>Automatic tags (docs/specs/tags.md, Automatic tags): applied with the summary, merged and retired nightly.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AutoTagTests(PostgresFixture db) : AiTestBase(db)
{
    protected override bool AutoTags => true;

    private Task<List<string>> Linked(Guid conversation) =>
        Db.QueryAsync<string>(
            "select t.name from conversation_tags l join tags t on t.id = l.tag_id where l.conversation_id = @conversation order by t.name",
            new { conversation });

    private Task<List<string>> Tags() => Db.QueryAsync<string>("select name from tags order by name");

    private async Task Rerun(Guid id)
    {
        await Db.ExecuteAsync("update conversations set ai_status = 'none', ai_through_segment_id = null where id = @id", new { id });
        await TickAndRun();
    }

    /// <summary>Runs the scheduler at 05:00 UTC of the next day, so the day's tidy is due, and the jobs it queues.</summary>
    private async Task Tidy(int days = 1)
    {
        Server.Time.SetUtcNow(new DateTimeOffset(Now.UtcDateTime.Date.AddDays(days).AddHours(5), TimeSpan.Zero));
        Llm.Respond = r => r.SchemaName == TidyTagsHandler.SchemaName ? Llm.TidyAnswer : FakeLlm.AnswerTagged("t", "s");
        await TickAndRun();
    }

    [Fact]
    public async Task A_proposed_tag_is_applied_at_once_as_the_tag_it_is_an_alias_of()
    {
        await Db.ExecuteAsync("insert into tags (id, name, created_at) values (gen_random_uuid(), 'repair', now())");
        await Db.ExecuteAsync("insert into tag_aliases (alias, tag_id, created_at) select 'ремонт', id, now() from tags");
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "Ремонт", "work");
        var id = await Seed(Talk, Talk);

        await TickAndRun();

        Assert.Equal(["repair", "work"], await Linked(id));
        Assert.True(await Db.ScalarAsync<bool>("select bool_and(auto) from conversation_tags where conversation_id = @id", new { id }));
        Assert.Equal(["work", "ремонт"], (await Db.QueryAsync<string>("select name from tag_suggestions where status = 'accepted'")).Order(StringComparer.Ordinal));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tag_suggestions where status = 'pending'"));
    }

    [Fact]
    public async Task A_tag_the_owner_removed_is_not_applied_again_when_the_summary_runs_again()
    {
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "work");
        var id = await Seed(Talk, Talk);
        await TickAndRun();
        Assert.True(await Server.Get<TagStore>().RemoveFromConversationAsync(id, "work", default));

        await Rerun(id);

        Assert.Empty(await Linked(id));
    }

    [Fact]
    public async Task The_nightly_tidy_merges_synonyms_into_the_most_used_tag_and_keeps_the_others_as_aliases()
    {
        var a = await Seed(Talk, Talk);
        var b = await SeedAt(Now.AddMinutes(-60), "closed", Talk, Talk);
        Llm.Respond = r => FakeLlm.AnswerTagged("t", "s", r.User.Contains(Talk, StringComparison.Ordinal) ? "repair" : "x");
        await TickAndRun();
        await Server.Get<TagStore>().AddToConversationAsync(a, "ремонт", Now, default);
        Llm.TidyAnswer = """{ "groups": [["ремонт", "repair", "invented"], ["repair", "work"]] }""";

        await Tidy();

        Assert.Equal(["repair"], await Tags());
        Assert.Equal(["repair"], await Linked(a));
        Assert.Equal(["repair"], await Linked(b));
        Assert.Equal(["ремонт"], await Db.QueryAsync<string>("select alias from tag_aliases"));
        Assert.Equal(1, await Db.ScalarAsync<int>("select sum(merged)::int from tag_tidy_runs"));
    }

    [Fact]
    public async Task A_tag_applied_once_is_retired_after_60_days_and_one_added_by_hand_stays()
    {
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "lonely");
        var id = await Seed(Talk, Talk);
        await TickAndRun();
        await Server.Get<TagStore>().AddToConversationAsync(id, "mine", Now, default);
        Llm.TidyAnswer = """{ "groups": [] }""";

        await Tidy(days: 30);
        Assert.Equal(["lonely", "mine"], await Linked(id));

        await Tidy(days: 31);
        Assert.Equal(["mine"], await Linked(id));
        Assert.Equal(["mine"], await Tags());
    }

    [Fact]
    public async Task The_tidy_runs_once_a_local_day_and_a_failed_run_is_not_retried_that_day()
    {
        var id = await Seed(Talk);
        await Server.Get<TagStore>().AddToConversationAsync(id, "a", Now, default);
        await Server.Get<TagStore>().AddToConversationAsync(id, "b", Now, default);
        Llm.TidyAnswer = "not json";

        await Tidy();
        Server.Time.Advance(TimeSpan.FromMinutes(30));
        await RunThreeAttempts();
        await TickAndRun();

        Assert.True(await Db.ScalarAsync<bool>("select failed from tag_tidy_runs where local_date = cast(@today as date)", new { today = Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }));
        Assert.Equal(0, await Db.ScalarAsync<long>($"select count(*) from jobs where kind = '{JobKinds.TidyTags}'"));
        Assert.Equal(3, Llm.Requests.Count(r => r.SchemaName == TidyTagsHandler.SchemaName));
    }

    [Fact]
    public async Task The_nightly_tidy_applies_proposals_left_pending_from_before_and_leaves_people_s_alone()
    {
        var id = await Seed(Talk);
        await Db.ExecuteAsync("insert into people (id, name, created_at) values ('018f0000-0000-7000-8000-0000000000c1', 'Anna', now())");
        await Db.ExecuteAsync(
            """
            insert into tag_suggestions (id, conversation_id, person_id, name, created_at) values
                (gen_random_uuid(), @id, null, 'garden', now()),
                (gen_random_uuid(), @id, '018f0000-0000-7000-8000-0000000000c1', 'neighbour', now())
            """,
            new { id });

        await Tidy();

        Assert.Equal(["garden"], await Linked(id));
        Assert.Equal(["neighbour"], await Db.QueryAsync<string>("select name from tag_suggestions where status = 'pending'"));
    }

    [Fact]
    public void The_kept_tag_is_the_most_used_and_names_not_in_use_are_ignored()
    {
        var at = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var groups = TidyTagsHandler.Groups(
            new TidyTagsHandler.Answer([["Repair", "ремонт", "nope"], ["ремонт", "fix"], ["solo"]]),
            [new TagUse("ремонт", 5, at), new TagUse("repair", 2, at.AddDays(-1)), new TagUse("fix", 9, at), new TagUse("solo", 1, at)]);

        Assert.Equal([["ремонт", "repair"]], groups);
    }
}
