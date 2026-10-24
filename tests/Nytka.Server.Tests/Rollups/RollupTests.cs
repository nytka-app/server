using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Rollups;
using Nytka.Storage;

namespace Nytka.Server.Tests.Rollups;

/// <summary>Summaries over time: a closed week from its digests, what changed, locked periods and the backfill.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RollupTests(PostgresFixture db) : Ai.AiTestBase(db)
{
    private static string Answer(string summary, params string[] changes) => JsonSerializer.Serialize(new { summary, changes });

    private void EnableRollups() => StartServer(settings => settings["Nytka:Rollups:Enabled"] = "true");

    private List<LlmRequest> Rollups() => [.. Llm.Requests.Where(r => r.SchemaName == RollupPrompt.SchemaName)];

    /// <summary>The week the scheduler makes: the last one that closed by yesterday (UTC, the default zone).</summary>
    private DateOnly LastWeek => RollupPeriod.LastClosed(RollupStore.Week, DigestDay.Today(Now, TimeZoneInfo.Utc).AddDays(-1));

    /// <summary>A day's digest and one summarized conversation that enrichment leaves alone.</summary>
    private async Task DayAsync(DateOnly date, string overview)
    {
        var at = new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
        var id = await SeedAt(at.AddMinutes(1), "closed", Talk);
        await Db.ExecuteAsync(
            """
            update conversations set ai_title = 'A talk', ai_summary = 'They talked.', ai_status = 'done', ai_updated_at = now(),
                ai_through_segment_id = (select max(id) from segments where conversation_id = @id)
            where id = @id;
            insert into digests (id, local_date, headline, overview, body, created_at)
            values (gen_random_uuid(), @date::date, 'Headline', @overview, '{"highlights":[],"decisions":[],"openQuestions":[]}', now());
            """,
            new { id, date = DigestDay.Text(date), overview });
    }

    [Fact]
    public async Task Off_by_default_nothing_is_made()
    {
        for (var i = 0; i < 3; i++)
        {
            await DayAsync(LastWeek.AddDays(i), $"Day {i}.");
        }

        await TickAndRun();

        Assert.Empty(Rollups());
        Assert.Equal(0, await Db.ScalarAsync<int>("select count(*)::int from rollups"));
    }

    [Fact]
    public async Task A_closed_week_with_three_days_is_summarized_from_its_digests_and_a_thin_one_is_locked_without_a_call()
    {
        EnableRollups();
        Llm.Respond = r => r.SchemaName == RollupPrompt.SchemaName ? Answer("A busy week.", "ignored, nothing came before") : FakeLlm.DefaultAnswer;
        for (var i = 0; i < 3; i++)
        {
            await DayAsync(LastWeek.AddDays(i), $"Overview of day {i}.");
        }

        await DayAsync(LastWeek.AddDays(-7), "Only day of the week before.");

        await TickAndRun();

        var week = Assert.Single(Rollups(), r => r.User.Contains("Overview of day 0.", StringComparison.Ordinal));
        Assert.Contains("Overview of day 2.", week.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Only day of the week before.", week.User, StringComparison.Ordinal);
        Assert.Contains("The period before:\n(none)", week.User, StringComparison.Ordinal);
        Assert.DoesNotContain(Rollups(), r => r.User.Contains("Only day of the week before.", StringComparison.Ordinal));

        var client = Server.CreateAuthorizedClient();
        var made = await client.GetFromJsonAsync<JsonElement>($"/api/v1/rollups/week/{DigestDay.Text(LastWeek)}");
        Assert.Equal(("A busy week.", false, 3, 3), (
            made.GetProperty("summary").GetString(), made.GetProperty("locked").GetBoolean(), made.GetProperty("sources").GetProperty("digests").GetInt32(),
            made.GetProperty("progress").GetProperty("have").GetInt32()));
        Assert.Empty(made.GetProperty("changes").EnumerateArray());
        Assert.Equal(DigestDay.Text(LastWeek.AddDays(7)), made.GetProperty("end").GetString());
    }

    [Fact]
    public async Task A_week_after_a_summarized_one_says_what_changed()
    {
        EnableRollups();
        Llm.Respond = r => r.SchemaName == RollupPrompt.SchemaName ? Answer("This week.", "Started tennis.") : FakeLlm.DefaultAnswer;
        await Db.ExecuteAsync(
            """
            insert into rollups (id, period, start_date, end_date, locked, have, need, summary, created_at, updated_at)
            values (gen_random_uuid(), 'week', @start::date, @end::date, false, 5, 3, 'The week before.', now(), now())
            """,
            new { start = DigestDay.Text(LastWeek.AddDays(-7)), end = DigestDay.Text(LastWeek) });
        for (var i = 0; i < 3; i++)
        {
            await DayAsync(LastWeek.AddDays(i), $"Day {i}.");
        }

        await TickAndRun();

        Assert.Contains("The period before:\nThe week before.", Rollups()[0].User, StringComparison.Ordinal);
        var made = await Server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/rollups/week/{DigestDay.Text(LastWeek)}");
        Assert.Equal(["Started tennis."], made.GetProperty("changes").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task A_week_with_too_little_is_locked_with_its_progress_and_costs_no_call()
    {
        EnableRollups();
        await DayAsync(LastWeek, "One day.");

        await TickAndRun();

        Assert.Empty(Rollups());
        var made = await Server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/rollups/week/{DigestDay.Text(LastWeek)}");
        Assert.True(made.GetProperty("locked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, made.GetProperty("summary").ValueKind);
        Assert.Equal(JsonValueKind.Null, made.GetProperty("changes").ValueKind);
        Assert.Equal((1, 3, "days"), (
            made.GetProperty("progress").GetProperty("have").GetInt32(), made.GetProperty("progress").GetProperty("need").GetInt32(),
            made.GetProperty("progress").GetProperty("unit").GetString()));
    }

    [Fact]
    public async Task A_topic_with_two_conversations_in_the_week_gets_its_own_week_from_their_summaries()
    {
        EnableRollups();
        Llm.Respond = r => r.SchemaName == RollupPrompt.SchemaName ? Answer("Tennis this week.") : FakeLlm.DefaultAnswer;
        for (var i = 0; i < 2; i++)
        {
            await DayAsync(LastWeek.AddDays(i), $"Day {i}.");
        }

        await Db.ExecuteAsync(
            """
            update conversations set ai_summary = 'They played tennis.', sensitivity = 'none';
            insert into tags (id, name, created_at) values (gen_random_uuid(), 'tennis', now());
            insert into conversation_tags (conversation_id, tag_id, created_at) select c.id, t.id, now() from conversations c, tags t;
            insert into topics (id, kind, tag_id, created_at, updated_at) select gen_random_uuid(), 'tag', id, now(), now() from tags;
            """);

        await TickAndRun();

        var topic = Assert.Single(Rollups(), r => r.System.Contains("tennis", StringComparison.Ordinal));
        Assert.Contains("They played tennis.", topic.User, StringComparison.Ordinal);
        var topicId = await Db.ScalarAsync<Guid>("select id from topics");
        var weeks = (await Server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/rollups?topicId={topicId}")).GetProperty("items");
        var week = Assert.Single(weeks.EnumerateArray());
        Assert.Equal(("Tennis this week.", "conversations"), (week.GetProperty("summary").GetString(), week.GetProperty("progress").GetProperty("unit").GetString()));
    }

    [Fact]
    public async Task The_backfill_is_refused_while_rollups_are_off_and_queues_every_closed_period_when_on()
    {
        await DayAsync(LastWeek.AddDays(-14), "Long ago.");
        Assert.Equal(HttpStatusCode.Conflict, (await Server.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/rollups/backfill", new { })).StatusCode);

        EnableRollups();
        var response = await Server.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/rollups/backfill", new { });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("queued").GetInt32() >= 4);
        Assert.Equal(HttpStatusCode.BadRequest, (await Server.CreateAuthorizedClient().GetAsync("/api/v1/rollups?period=day")).StatusCode);
    }
}
