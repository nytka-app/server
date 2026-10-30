using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Situations;
using Nytka.Storage;

namespace Nytka.Server.Tests.Situations;

/// <summary>
/// Situations end to end (nytka-app/server#304) on a real database: a closed day is cut into stored situations by the speaker tracks of its
/// lines, a rebuild changes nothing, each situation is summarized on its own, and the Timeline follows <c>situations.enabled</c>.
/// </summary>
public sealed class SituationTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private const string Day = "2026-09-28";

    private static readonly Guid Conversation = Guid.Parse("018f0000-0000-7000-8000-000000000001");
    private static readonly Guid TrackA = Guid.Parse("018f0000-0000-7000-8000-0000000000a1");
    private static readonly Guid TrackB = Guid.Parse("018f0000-0000-7000-8000-0000000000b1");
    private static readonly DateTime Start = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeLlm _llm = new();
    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = Server(situations: true);
        await Seed();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private NytkaApiFactory Server(bool situations) => new(
        db,
        settings =>
        {
            settings["Nytka:Memories:Enabled"] = "false";
            settings["Nytka:Tracks:Enabled"] = "true";
            if (situations)
            {
                settings["Nytka:Situations:Enabled"] = "true";
            }
        },
        services => services.AddSingleton<ILlmClient>(_llm));

    private HttpClient Admin => _server.CreateAuthorizedClient();

    /// <summary>
    /// 09:00 to 09:30. Track A talks every minute to 09:29, B every two minutes from 09:12 to 09:24, the wearer every two minutes to 09:27;
    /// the conversation's own summary is stored, the topic boundaries are not.
    /// </summary>
    private async Task Seed()
    {
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, ai_title, ai_summary, ai_status, ai_updated_at, created_at, updated_at)
            values (@id, @start, @start + interval '30 minutes', 'closed', 'Lunch with Anna', 'They talked.', 'done', @start, @start, @start);
            insert into transcription_batches (id, conversation_id, started_at, ended_at, status, offset_map, created_at)
            overriding system value values (1, @id, @start, @start, 'done', '[]', @start);
            insert into speaker_tracks (id, day, model, centroid, seconds, segments, ordinal, built_at)
            values (@a, @day, 'm', '\x00', 1, 1, 1, @start), (@b, @day, 'm', '\x00', 1, 1, 2, @start);
            insert into tracks_state (id, enabled, updated_at) values (1, true, @start)
            on conflict (id) do update set enabled = true;
            """,
            new { id = Conversation, start = Start, a = TrackA, b = TrackB, day = new DateOnly(2026, 9, 28) });
        for (var minute = 0; minute < 30; minute++)
        {
            await Line(minute * 60, TrackA, user: false);
            if (minute is >= 12 and <= 24 && minute % 2 == 0)
            {
                await Line((minute * 60) + 20, TrackB, user: false);
            }

            if (minute % 2 == 1 && minute < 28)
            {
                await Line((minute * 60) + 30, null, user: true);
            }
        }
    }

    private Task Line(int seconds, Guid? track, bool user) => db.ExecuteAsync(
        """
        insert into segments (conversation_id, batch_id, started_at, ended_at, text, track_id, is_user)
        values (@conversation, 1, @from, @until, 'we were talking about the plan for the week and then more', @track, @user)
        """,
        new { conversation = Conversation, from = Start.AddSeconds(seconds), until = Start.AddSeconds(seconds + 4), track, user = user ? (bool?)true : null });

    private async Task Build()
    {
        (await Admin.PostAsJsonAsync("/api/v1/situations/build", new { day = Day })).EnsureSuccessStatusCode();
        await _server.RunJobsAsync();
    }

    private async Task Ticks(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await _server.Get<Scheduler>().TickAsync(default);
            await _server.RunJobsAsync();
        }
    }

    private Task<string> Snapshot() => db.ScalarAsync<string>(
        """
        select coalesce(jsonb_agg(to_jsonb(s) - 'built_at' order by s.started_at), '[]')::text || '|'
            || coalesce((select jsonb_agg(ss order by ss.segment_id)::text from situation_segments ss), '[]')
        from situations s
        """);

    [Fact]
    public async Task A_day_is_cut_where_the_set_of_present_tracks_changes()
    {
        await Build();

        var rows = await db.QueryAsync<(DateTime Start, DateTime End, string StartsWith, Guid[] Tracks, int Version, string Activity)>(
            "select started_at, ended_at, starts_with, track_ids, version, activity from situations order by started_at");
        Assert.Equal(3, rows.Count);
        // B joins with her first line at 09:12:20 and leaves with her last one, which ends at 09:24:24, while A talks on.
        Assert.Equal([Start, Start.AddMinutes(12).AddSeconds(20), Start.AddMinutes(24).AddSeconds(24)], rows.Select(r => r.Start));
        Assert.Equal(["conversation_started", "person_joined", "person_left"], rows.Select(r => r.StartsWith));
        Assert.Equal([[TrackA], new[] { TrackA, TrackB }.Order().ToArray(), [TrackA]], rows.Select(r => r.Tracks));
        Assert.All(rows, r => Assert.Equal((1, "conversation"), (r.Version, r.Activity)));
        Assert.Equal(Start.AddMinutes(30), rows[^1].End);
        Assert.Equal("final", await db.ScalarAsync<string>("select stage from situation_days where day = '2026-09-28'"));
        // Every line is in exactly one situation.
        Assert.Equal(await db.ScalarAsync<long>("select count(*) from segments"), await db.ScalarAsync<long>("select count(*) from situation_segments"));
    }

    [Fact]
    public async Task Rebuilding_a_day_twice_gives_identical_situations()
    {
        await Build();
        var first = await Snapshot();

        await Build();
        var second = await Snapshot();
        await db.ExecuteAsync("delete from situations");
        await Build();
        var afterDelete = await Snapshot();

        Assert.Equal(first, second);
        Assert.Equal(first, afterDelete);
        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from situations"));
    }

    [Fact]
    public async Task A_line_that_arrives_late_changes_its_situation_by_a_version_and_leaves_the_others()
    {
        await Build();
        const string Others = "select (jsonb_agg(to_jsonb(s) order by started_at))::text from situations s where started_at > '2026-09-28 09:05'";
        var untouched = await db.ScalarAsync<string>(Others);

        await Line(300, TrackA, user: false);
        await Build();

        Assert.Equal([2, 1, 1], await db.QueryAsync<int>("select version from situations order by started_at"));
        Assert.Equal(untouched, await db.ScalarAsync<string>(Others));
        Assert.Equal(await db.ScalarAsync<long>("select count(*) from segments"), await db.ScalarAsync<long>("select count(*) from situation_segments"));
    }

    [Fact]
    public async Task With_situations_off_a_build_does_nothing_and_the_route_says_so()
    {
        _server.Dispose();
        _server = Server(situations: false);

        var response = await Admin.PostAsJsonAsync("/api/v1/situations/build", new { day = Day });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PostAsJsonAsync("/api/v1/situations/build", new { day = "yesterday" })).StatusCode);
        await _server.Get<JobQueue>().EnqueueAsync(
            JobKinds.BuildSituations, new Nytka.Server.Pipeline.SituationsPayload(Day), null, DateTimeOffset.UtcNow, default);
        await _server.RunJobsAsync();
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from situations"));
    }

    [Fact]
    public async Task The_scan_cuts_an_ended_day_once_and_summarizes_each_situation_on_its_own_lines()
    {
        _llm.Respond = request => request.SchemaName == SituationPrompt.SchemaName
            ? JsonSerializer.Serialize(new { title = "A part of the day", summary = "Some of them talked." })
            : FakeLlm.DefaultAnswer;
        _server.Time.SetUtcNow(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));

        // The scan waits for the conversation's own summary, cuts the day, then asks for the situations' summaries.
        await Ticks(4);

        var asked = _llm.Requests.Where(r => r.SchemaName == SituationPrompt.SchemaName).ToList();
        Assert.Equal(3, asked.Count);
        // Each prompt holds its situation's lines only: 19, 25 and 7 of the 51 lines of the half hour.
        Assert.Equal([7, 19, 25], asked.Select(r => r.User.Count(c => c == '#')).Order());
        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from situations where title = 'A part of the day'"));
        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from situations where ai_status = 'done' and ai_lines_hash = lines_hash"));
        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from situations where version = 1"));

        // Another pass asks nothing more.
        await Ticks(2);
        Assert.Equal(3, _llm.Requests.Count(r => r.SchemaName == SituationPrompt.SchemaName));
    }

    [Fact]
    public async Task A_day_that_has_not_ended_is_not_cut_by_the_scan()
    {
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, ai_status, created_at, updated_at)
            values (gen_random_uuid(), '2026-09-29 08:00', '2026-09-29 08:30', 'closed', 'done', '2026-09-29 08:00', '2026-09-29 08:00')
            """);

        await Ticks(3);

        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from situations where day = '2026-09-28'"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from situations where day = '2026-09-29'"));
    }

    [Fact]
    public async Task A_day_waits_for_a_conversation_still_being_summarized()
    {
        await db.ExecuteAsync("update conversations set ai_status = 'pending'");
        _server.Time.SetUtcNow(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));

        await _server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'build-situations'"));
    }

    [Fact]
    public async Task The_timeline_returns_situations_with_a_version_when_on_and_is_byte_identical_to_before_when_off()
    {
        _server.Dispose();
        _server = Server(situations: false);
        var before = await Admin.GetStringAsync($"/api/v1/timeline?date={Day}");
        _server.Dispose();
        _server = Server(situations: true);
        await Build();

        var on = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/timeline?date={Day}");
        _server.Dispose();
        _server = Server(situations: false);
        var off = await Admin.GetStringAsync($"/api/v1/timeline?date={Day}");

        var scenes = on.GetProperty("scenes").EnumerateArray().ToList();
        Assert.Equal(3, scenes.Count);
        Assert.Equal(
            await db.QueryAsync<string>("select id::text from situations order by started_at"), scenes.Select(s => s.GetProperty("id").GetString()));
        Assert.All(scenes, s => Assert.Equal((Conversation, 1), (s.GetProperty("conversationId").GetGuid(), s.GetProperty("version").GetInt32())));
        Assert.Equal(["conversation_started", "person_joined", "person_left"], scenes.Select(s => s.GetProperty("startsWith").GetString()));
        Assert.Equal([["Voice 1"], ["Voice 1", "Voice 2"], ["Voice 1"]], scenes.Select(s => s.GetProperty("people").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList()));
        Assert.Equal("Lunch with Anna", scenes[0].GetProperty("title").GetString());
        Assert.DoesNotContain("startsWith", before, StringComparison.Ordinal);
        Assert.DoesNotContain("version", before, StringComparison.Ordinal);
        Assert.Equal(before, off);
    }

    [Fact]
    public async Task A_day_not_built_yet_shows_as_the_scenes_it_would_be_cut_into_with_version_0()
    {
        var on = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/timeline?date={Day}");

        var scene = Assert.Single(on.GetProperty("scenes").EnumerateArray());
        Assert.Equal(0, scene.GetProperty("version").GetInt32());
        Assert.Equal(Conversation.ToString(), scene.GetProperty("id").GetString());
    }

    /// <summary>The query the docs give for scoring the golden boundary answers: the stored cuts find the boundary the conversation rule misses.</summary>
    [Fact]
    public async Task The_documented_golden_boundary_query_scores_the_situations_against_the_conversation_rule()
    {
        await Build();
        long At(string time) => db.ScalarAsync<long>("select id from segments where started_at = @at", new { at = DateTime.Parse($"2026-09-28 {time}Z").ToUniversalTime() }).GetAwaiter().GetResult();
        await db.ExecuteAsync(
            """
            insert into golden_labels (kind, segment_id, answer, other_segment_id, answered_at) values
                ('boundary', @joined, 'new-situation', @before, now()), ('boundary', @same, 'same-situation', @earlier, now())
            """,
            new { joined = At("09:12:20"), before = At("09:12:00"), same = At("09:05:00"), earlier = At("09:04:00") });
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "docs", "explanation", "features.md")))
        {
            root = root.Parent!;
        }

        var docs = await File.ReadAllTextAsync(Path.Combine(root.FullName, "docs", "explanation", "features.md"));
        var sql = docs[(docs.LastIndexOf("```sql", StringComparison.Ordinal) + 6)..];
        sql = sql[..sql.IndexOf("```", StringComparison.Ordinal)];

        var rows = await db.QueryAsync<(string Run, long Tp, long Fp, long Fn, decimal? F1)>(sql);

        Assert.Equal([("before", 0L, 0L, 1L, 0m), ("after", 1L, 0L, 0L, 1m)], rows.Select(r => (r.Run, r.Tp, r.Fp, r.Fn, r.F1)));
    }
}
