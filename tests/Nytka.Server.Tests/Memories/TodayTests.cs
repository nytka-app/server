using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Api;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Memories;

public sealed class TodayPickTests
{
    private static readonly DateTime DayEnd = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

    private static DayCandidate Task(
        string kind = TaskKinds.Commitment, string? salience = "ordinary", Guid? person = null, DateTime? due = null, int minute = 0,
        string text = "t") =>
        new()
        {
            Kind = "task", Id = Guid.CreateVersion7(), Text = text, Salience = salience, TaskKind = kind, PersonId = person, Due = due,
            At = new DateTime(2026, 9, 29, 9, minute, 0, DateTimeKind.Utc),
        };

    [Fact]
    public void Vital_anything_and_commitments_with_a_person_or_a_due_date_and_what_is_owed_are_eligible()
    {
        Assert.True(TodayPick.Eligible(Task(TaskKinds.Idea, "vital")));
        Assert.True(TodayPick.Eligible(Task(person: Guid.NewGuid())));
        Assert.True(TodayPick.Eligible(Task(due: DayEnd)));
        Assert.True(TodayPick.Eligible(Task(TaskKinds.WaitingOn)));
        Assert.True(TodayPick.Eligible(new DayCandidate { Kind = "memory", Salience = "vital" }));
        Assert.True(TodayPick.Eligible(new DayCandidate { Kind = "note", Salience = "vital" }));
    }

    [Fact]
    public void Noise_a_bare_commitment_an_idea_and_an_unrated_memory_are_not()
    {
        Assert.False(TodayPick.Eligible(Task(salience: "noise", person: Guid.NewGuid())));
        Assert.False(TodayPick.Eligible(Task(TaskKinds.WaitingOn, "noise")));
        Assert.False(TodayPick.Eligible(Task()));
        Assert.False(TodayPick.Eligible(Task(TaskKinds.Idea, "ordinary", Guid.NewGuid(), DayEnd)));
        Assert.False(TodayPick.Eligible(new DayCandidate { Kind = "memory", Salience = "ordinary" }));
        Assert.False(TodayPick.Eligible(new DayCandidate { Kind = "memory", Salience = null }));
    }

    [Fact]
    public void A_mark_of_noise_beats_everything_else()
    {
        Assert.False(TodayPick.Eligible(Task(TaskKinds.WaitingOn, "noise", Guid.NewGuid(), DayEnd.AddHours(-1))));
    }

    [Fact]
    public void At_most_seven_are_shown_the_best_ranked_first_and_listed_in_the_order_they_were_said()
    {
        var overdue = Task(due: DayEnd.AddDays(-3), minute: 5, text: "overdue");
        var vital = Task(TaskKinds.Idea, "vital", minute: 1, text: "vital");
        var owed = Task(TaskKinds.WaitingOn, person: Guid.NewGuid(), minute: 2, text: "owed");
        var filler = Enumerable.Range(10, 8).Select(m => Task(person: Guid.NewGuid(), minute: m, text: $"p{m}")).ToList();

        var picked = TodayPick.Choose([.. filler, overdue, vital, owed], DayEnd);

        Assert.Equal(TodayPick.Max, picked.Count);
        Assert.Contains(overdue, picked);
        Assert.Contains(vital, picked);
        Assert.Contains(owed, picked);
        Assert.Equal(picked.OrderBy(c => c.At).Select(c => c.Id), picked.Select(c => c.Id));
        // The four with the weakest rank and the oldest time are the ones left out.
        Assert.DoesNotContain(filler[0], picked);
    }

    [Theory]
    [InlineData("2026-09-29T08:59:00Z", "morning")]  // 11:59 in Kyiv (UTC+3)
    [InlineData("2026-09-29T09:00:00Z", "day")]      // 12:00
    [InlineData("2026-09-29T14:59:00Z", "day")]      // 17:59
    [InlineData("2026-09-29T15:00:00Z", "evening")]  // 18:00
    public void The_part_of_the_day_follows_the_local_hour(string at, string part)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var from = new DateTime(2026, 9, 28, 21, 0, 0, DateTimeKind.Utc);
        var item = new DayItem("task", Guid.NewGuid(), "t", null, false, null, DateTime.Parse(at).ToUniversalTime(), null, null, null, null, false, []);

        Assert.Equal(part, TodayEndpoints.Part(item, from, from.AddDays(1), zone));
    }

    [Fact]
    public void A_task_said_earlier_but_due_today_takes_its_part_from_the_due_time_or_is_day_without_one()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var from = new DateTime(2026, 9, 28, 21, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);
        DayItem Said(DateTime due) => new("task", Guid.NewGuid(), "t", null, false, null, from.AddDays(-3), null, null, null, due, false, []);

        Assert.Equal("evening", TodayEndpoints.Part(Said(new DateTime(2026, 9, 29, 16, 30, 0, DateTimeKind.Utc)), from, to, zone));
        Assert.Equal("day", TodayEndpoints.Part(Said(new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc)), from, to, zone));
        Assert.Equal("day", TodayEndpoints.Part(Said(from.AddDays(-1)), from, to, zone));
    }
}

public sealed class TodayApiTests(PostgresFixture db) : MemoryTestBase(db)
{
    private static Guid Id(int n) => Guid.Parse($"018f0000-0000-7000-8000-0000000000{n:x2}");

    private static readonly Guid Olena = Guid.Parse("018f0000-0000-7000-8000-0000000000f1");

    private HttpClient Admin => Server.CreateAuthorizedClient();

    private Task<long> Line(string text) => Db.ScalarAsync<long>("select id from segments where text = @text", new { text });

    private Task Task(
        int n, string text, string kind = "commitment", string? salience = "ordinary", bool done = false, string? sensitivity = null,
        Guid? person = null, DateTime? due = null, bool deleted = false, Guid? conversation = null) =>
        Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, kind, salience, done, sensitivity, person_id, due_at, deleted_at, created_at, updated_at)
            values (@id, @conversation, @text, @fingerprint, @kind, @salience, @done, @sensitivity, @person, @due, case when @deleted then @start end, @start, @start)
            """,
            new
            {
                id = Id(n), conversation = conversation ?? Conversation, text, fingerprint = TextFingerprint.Of(text), kind, salience, done,
                sensitivity, person, due, deleted, start = Start,
            });

    private Task Memory(int n, string text, string? salience = "vital", bool confirmed = true, string? sensitivity = null) =>
        Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, conversation_id, salience, confirmed, sensitivity, created_at, updated_at)
            values (@id, @text, @fingerprint, 'ai', @conversation, @salience, @confirmed, @sensitivity, @start, @start)
            """,
            new { id = Id(n), text, fingerprint = TextFingerprint.Of(text), conversation = Conversation, salience, confirmed, sensitivity, start = Start });

    private async Task<JsonElement> Today(string query = "")
    {
        Time.SetUtcNow(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero));
        return await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/today{query}");
    }

    private static List<Guid> Ids(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    [Fact]
    public async Task Today_shows_what_the_rule_picks_with_evidence_and_nothing_private()
    {
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@id, 'Olena', now())", new { id = Olena });
        var mine = await Line("I run every morning.");
        await Db.ExecuteAsync("update segments set is_user = true where id = @mine", new { mine });
        await Task(1, "Send the lease draft", person: Olena);                       // a named person: shown
        await Task(2, "Think about a name for the dog");                             // a bare commitment: not shown
        await Task(3, "Try the new bakery", "idea", "vital");                        // vital: shown
        await Task(4, "Call the doctor", "commitment", "vital", sensitivity: "health"); // private: never
        await Task(5, "Already done", person: Olena, done: true);                    // done: not shown
        await Task(6, "Noise with a person", salience: "noise", person: Olena);      // noise: not shown
        await Task(7, "Deleted one", person: Olena, deleted: true);                  // deleted: not shown
        await Task(8, "Pay the invoice", due: new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc), conversation: Other); // overdue: shown
        await Memory(9, "You run every morning.");
        await Memory(10, "Ordinary memory", "ordinary");
        await Memory(11, "Unconfirmed memory", confirmed: false);
        await Db.ExecuteAsync("insert into memory_sources (memory_id, segment_id) values (@id, @mine)", new { id = Id(9), mine });
        await Db.ExecuteAsync("insert into task_sources (task_id, segment_id) values (@id, @mine)", new { id = Id(1), mine });

        var today = await Today();

        Assert.Equal("2026-09-29", today.GetProperty("date").GetString());
        Assert.Equal([Id(1), Id(3), Id(8), Id(9)], Ids(today).Order());
        var lease = today.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == Id(1));
        Assert.Equal("task", lease.GetProperty("kind").GetString());
        Assert.Equal("Olena", lease.GetProperty("personName").GetString());
        Assert.Equal("morning", lease.GetProperty("part").GetString());
        var evidence = Assert.Single(lease.GetProperty("evidence").EnumerateArray());
        Assert.Equal(("Wearer", true, "I run every morning."), (evidence.GetProperty("speaker").GetString(), evidence.GetProperty("isUser").GetBoolean(), evidence.GetProperty("quote").GetString()));
        Assert.Equal(mine, evidence.GetProperty("segmentId").GetInt64());
        Assert.Equal(Conversation, evidence.GetProperty("conversationId").GetGuid());
        Assert.Null(lease.GetProperty("sensitivity").GetString());
    }

    [Fact]
    public async Task Today_is_seven_items_at_most_and_nothing_a_class_marks_even_for_admin()
    {
        for (var n = 1; n <= 9; n++)
        {
            await Task(n, $"Task number {n}", "commitment", "vital");
        }

        await Task(20, "Private but vital", "commitment", "vital", sensitivity: "money");
        await Task(21, "Explicitly none", "commitment", "vital", sensitivity: "none");

        var ids = Ids(await Today());

        Assert.Equal(7, ids.Count);
        Assert.DoesNotContain(Id(20), ids);
    }

    [Fact]
    public async Task A_mark_of_noise_takes_a_vital_task_off_Today()
    {
        await Task(1, "Send the lease draft", "commitment", "vital");
        Assert.Contains(Id(1), Ids(await Today()));

        await Admin.PatchAsync($"/api/v1/tasks/{Id(1)}", new StringContent("""{ "salience": "noise" }""", System.Text.Encoding.UTF8, "application/json"));

        Assert.DoesNotContain(Id(1), Ids(await Today()));
    }

    [Fact]
    public async Task A_long_quote_is_cut_and_an_unlabelled_line_is_Unknown()
    {
        var anna = await Line("My sister Olena lives in Lviv.");
        var unlabelled = await Line("I run every morning.");
        await Db.ExecuteAsync("update segments set text = @text where id = @id", new { id = unlabelled, text = new string('x', 400) });
        await Task(1, "A task with two lines", "commitment", "vital");
        await Db.ExecuteAsync("insert into task_sources (task_id, segment_id) values (@t, @a), (@t, @u)", new { t = Id(1), a = anna, u = unlabelled });

        var item = (await Today()).GetProperty("items")[0];

        var lines = item.GetProperty("evidence").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Anna", lines[0].GetProperty("speaker").GetString());
        Assert.Equal("Unknown", lines[1].GetProperty("speaker").GetString());
        Assert.Equal(TodayStore.QuoteLength, lines[1].GetProperty("quote").GetString()!.Length);
    }

    [Fact]
    public async Task The_timeline_is_the_day_as_scenes_with_every_live_item()
    {
        var mine = await Line("I run every morning.");
        await Db.ExecuteAsync("update segments set is_user = true where id = @mine", new { mine });
        await Task(1, "Send the lease draft", salience: "noise", done: true);
        await Task(2, "Another", "idea", "vital");
        await Task(3, "Gone", deleted: true);
        await Memory(4, "You run every morning.", "ordinary");
        await Memory(5, "Hidden unconfirmed", confirmed: false);
        await Db.ExecuteAsync("insert into notes (id, conversation_id, topic, points, salience, created_at, updated_at) values (@id, @c, 'running', array['Run before breakfast'], 'ordinary', now(), now())", new { id = Id(6), c = Conversation });

        var timeline = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/timeline?date=2026-09-29");

        Assert.Equal("2026-09-29", timeline.GetProperty("date").GetString());
        var scenes = timeline.GetProperty("scenes").EnumerateArray().ToList();
        Assert.Equal([Conversation, Other], scenes.Select(s => s.GetProperty("conversationId").GetGuid()));
        var first = scenes[0];
        Assert.Equal(Conversation.ToString(), first.GetProperty("id").GetString());
        Assert.Equal("Lunch with Anna", first.GetProperty("title").GetString());
        Assert.False(first.GetProperty("noise").GetBoolean());
        Assert.True(scenes[1].GetProperty("noise").GetBoolean());
        Assert.Equal(["Anna"], first.GetProperty("people").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("place").ValueKind);
        var items = first.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([Id(1), Id(2), Id(4), Id(6)], items.Select(i => i.GetProperty("id").GetGuid()).Order());
        var note = items.Single(i => i.GetProperty("kind").GetString() == "note");
        Assert.Equal("running: Run before breakfast", note.GetProperty("text").GetString());
        Assert.True(items.Single(i => i.GetProperty("id").GetGuid() == Id(1)).GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task The_timeline_hides_a_private_scene_and_item_from_a_read_token_but_not_from_admin_or_a_sensitive_token()
    {
        await Db.ExecuteAsync("update conversations set sensitivity = 'therapy' where id = @id", new { id = Other });
        await Task(1, "A private task", sensitivity: "money");

        var admin = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/timeline?date=2026-09-29");
        var read = await Server.CreateClientWithScope("read").GetFromJsonAsync<JsonElement>("/api/v1/timeline?date=2026-09-29");
        var trusted = await Server.CreateClientWithScope("read", sensitive: true).GetFromJsonAsync<JsonElement>("/api/v1/timeline?date=2026-09-29");

        Assert.Equal(2, admin.GetProperty("scenes").GetArrayLength());
        Assert.Equal("therapy", admin.GetProperty("scenes")[1].GetProperty("sensitivity").GetString());
        Assert.Equal(2, trusted.GetProperty("scenes").GetArrayLength());
        var readScenes = read.GetProperty("scenes");
        Assert.Equal(1, readScenes.GetArrayLength());
        Assert.Empty(readScenes[0].GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_bad_zone_or_date_is_a_400_and_a_read_token_may_ask()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/today?tz=Mars/Base")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/timeline?tz=Mars/Base")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/timeline?date=yesterday")).StatusCode);
        var read = Server.CreateClientWithScope("read");
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/today")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/timeline")).StatusCode);
    }

    [Fact]
    public async Task The_day_is_the_owners_local_day()
    {
        await Task(1, "Send the lease draft", "commitment", "vital");
        Time.SetUtcNow(new DateTimeOffset(2026, 9, 29, 22, 30, 0, TimeSpan.Zero)); // 01:30 on the 30th in Kyiv

        var kyiv = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/today?tz=Europe/Kyiv");
        var utc = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/today?tz=UTC");

        Assert.Equal("2026-09-30", kyiv.GetProperty("date").GetString());
        Assert.Empty(kyiv.GetProperty("items").EnumerateArray());
        Assert.Equal("2026-09-29", utc.GetProperty("date").GetString());
        Assert.Equal([Id(1)], Ids(utc));
    }
}
