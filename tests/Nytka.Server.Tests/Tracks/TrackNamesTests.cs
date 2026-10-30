using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Audio.Voice;
using Nytka.Server.Jobs;
using Nytka.Server.Voice;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Tracks;

/// <summary>
/// Names for speaker tracks from phone calls and the calendar end to end (nytka-app/server#302) on a real database: the scheduler
/// queues <c>name-tracks</c> once a day's tracks are built, and what it makes is a suggestion the owner decides on.
/// </summary>
public sealed class TrackNamesTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private const string Model = "fake-model";
    private const int Size = 192;
    private const string Day = "2026-09-29";

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(StartMs);

    private NytkaApiFactory? _server;

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server?.Dispose();
        return Task.CompletedTask;
    }

    private void Boot(Action<IDictionary<string, string?>>? configure = null) => _server = new NytkaApiFactory(
        db,
        settings =>
        {
            settings["Nytka:Tracks:Enabled"] = "true";
            configure?.Invoke(settings);
        },
        services => services.AddSingleton(new SpeakerModel(new FakeEmbedder(), Model)));

    private NytkaApiFactory Server => _server!;

    private HttpClient Client => Server.CreateAuthorizedClient();

    private static float[] Voice(int axis)
    {
        var vector = new float[Size];
        vector[axis] = 1;
        vector[100 + axis] = 0.1f;
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        return vector.Select(v => v / norm).ToArray();
    }

    private async Task<(Guid Conversation, long Batch)> Conversation()
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@id, @at, @at, 'closed', @at, @at)",
            new { id, at = Start });
        var batch = await db.ScalarAsync<long>(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@id, @at, @at, 'done', '{}', @at) returning id",
            new { id, at = Start });
        return (id, batch);
    }

    private async Task Line((Guid Conversation, long Batch) c, double start, double seconds, int voice, bool user = false)
    {
        var id = await db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, is_user)
            values (@Conversation, @Batch, @from, @until, 'line', @user) returning id
            """,
            new { c.Conversation, c.Batch, from = Start.AddSeconds(start), until = Start.AddSeconds(start + seconds), user });
        await db.ExecuteAsync(
            "insert into track_prints (segment_id, idx, batch_id, model, fingerprint, created_at) values (@id, 0, @batch, @Model, @print, now())",
            new { id, batch = c.Batch, Model, print = VoiceStore.Encode(Voice(voice)) });
    }

    private Task CallRange(double start, double seconds, string? contact, string kind = "call") => db.ExecuteAsync(
        "insert into context_ranges (id, kind, route, started_at, ended_at, contact, received_at) values (@id, @kind, 'speaker', @from, @until, @contact, @at)",
        new { id = Guid.NewGuid(), kind, from = Start.AddSeconds(start), until = Start.AddSeconds(start + seconds), contact, at = Start });

    /// <summary>The next day has come: tracks are built, then named.</summary>
    private async Task Build()
    {
        Server.Time.Advance(TimeSpan.FromDays(1));
        await Tick();
        await Tick();
    }

    private async Task Tick()
    {
        await Server.Get<Scheduler>().TickAsync(default);
        await Server.RunJobsAsync();
    }

    private async Task<JsonElement[]> Suggestions() =>
        (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/tracks?day={Day}")).GetProperty("tracks").EnumerateArray()
            .SelectMany(t => t.GetProperty("suggestions").EnumerateArray()).ToArray();

    /// <summary>The owner speaks for 30 s, then the other voice for 40 s: one call, two tracks.</summary>
    private async Task OneVoiceOnTheLine()
    {
        var c = await Conversation();
        await Line(c, 0, 30, 0, user: true);
        await Line(c, 30, 40, 1);
        await CallRange(0, 80, "Olena K.");
    }

    [Fact]
    public async Task A_call_range_with_a_contact_and_one_other_voice_gives_one_call_suggestion_that_waits_for_the_owner()
    {
        Boot();
        await OneVoiceOnTheLine();

        await Build();

        var suggestion = Assert.Single(await Suggestions());
        Assert.Equal("call", suggestion.GetProperty("kind").GetString());
        Assert.Equal("Olena K.", suggestion.GetProperty("name").GetString());
        Assert.Equal(0.9, suggestion.GetProperty("similarity").GetDouble(), 3);
        Assert.Equal(JsonValueKind.Null, suggestion.GetProperty("personId").ValueKind);
        Assert.True(suggestion.GetProperty("evidence").TryGetProperty("range", out _));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from speaker_tracks where person_id is not null"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from people"));

        var decide = await Client.PostAsJsonAsync($"/api/v1/tracks/suggestions/{suggestion.GetProperty("id").GetGuid()}", new { decision = "accept" });

        Assert.Equal(HttpStatusCode.NoContent, decide.StatusCode);
        Assert.Equal("Olena K.", await db.ScalarAsync<string>("select p.name from speaker_tracks t join people p on p.id = t.person_id"));
    }

    [Fact]
    public async Task A_contact_who_is_already_a_person_is_suggested_as_that_person()
    {
        Boot();
        await OneVoiceOnTheLine();
        var person = await Server.Get<PeopleStore>().CreateAsync("olena k.", Server.Time.GetUtcNow(), default);

        await Build();

        Assert.Equal(person, Assert.Single(await Suggestions()).GetProperty("personId").GetGuid());
    }

    [Fact]
    public async Task Two_other_voices_of_similar_length_in_a_call_give_no_suggestion()
    {
        Boot();
        var c = await Conversation();
        await Line(c, 0, 20, 0, user: true);
        await Line(c, 20, 30, 1);
        await Line(c, 50, 30, 2);
        await CallRange(0, 90, "Olena K.");

        await Build();

        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));
        Assert.Empty(await Suggestions());
    }

    [Fact]
    public async Task Nothing_is_suggested_while_call_names_are_off_or_for_a_call_with_no_contact()
    {
        Boot(settings => settings["Nytka:People:CallNames"] = "false");
        await OneVoiceOnTheLine();
        await Build();
        Assert.Empty(await Suggestions());
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'name-tracks'"));

        Server.Dispose();
        Boot();
        await db.ExecuteAsync("update context_ranges set contact = null");
        await db.ExecuteAsync("delete from track_name_runs");
        await Tick();
        Assert.Empty(await Suggestions());
    }

    [Fact]
    public async Task A_rebuild_keeps_the_suggestion_and_a_rejected_name_is_not_offered_again()
    {
        Boot();
        await OneVoiceOnTheLine();
        await Build();
        var first = Assert.Single(await Suggestions()).GetProperty("id").GetGuid();

        await Client.PostAsJsonAsync("/api/v1/tracks/build", new { day = Day });
        await Server.RunJobsAsync();
        await Tick();
        Assert.Equal(first, Assert.Single(await Suggestions()).GetProperty("id").GetGuid());

        await Client.PostAsJsonAsync($"/api/v1/tracks/suggestions/{first}", new { decision = "reject" });
        await Client.PostAsJsonAsync("/api/v1/tracks/build", new { day = Day });
        await Server.RunJobsAsync();
        await Tick();

        Assert.Empty(await Suggestions());
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from track_suggestions where status = 'rejected'"));
    }

    [Fact]
    public async Task A_call_range_that_arrives_after_the_day_was_named_names_it_on_the_next_tick()
    {
        Boot();
        var c = await Conversation();
        await Line(c, 0, 30, 0, user: true);
        await Line(c, 30, 40, 1);
        await Build();
        Assert.Empty(await Suggestions());

        Server.Time.Advance(TimeSpan.FromMinutes(1));
        await CallRange(0, 80, "Olena K.");
        await db.ExecuteAsync("update context_ranges set received_at = @at", new { at = Server.Time.GetUtcNow() });
        await Tick();

        Assert.Equal("Olena K.", Assert.Single(await Suggestions()).GetProperty("name").GetString());
    }

    private async Task Meeting(params string[] attendees) => await db.ExecuteAsync(
        "insert into calendar_events (uid, starts_at, ends_at, title, attendees, fetched_at) values ('uid-1', @from, @until, 'Planning', @attendees, @from)",
        new { from = Start, until = Start.AddMinutes(10), attendees });

    [Fact]
    public async Task A_calendar_attendee_is_a_meeting_suggestion_at_0_6_that_is_never_applied_even_with_auto_apply_at_its_lowest()
    {
        Boot(settings =>
        {
            settings["Nytka:People:CalendarNames"] = "true";
            settings["Nytka:People:AutoApplyNames"] = "true";
            settings["Nytka:People:AutoApplyConfidence"] = "0.5";
        });
        var c = await Conversation();
        await Line(c, 0, 30, 0, user: true);
        await Line(c, 30, 40, 1);
        await Meeting("Taras");

        await Build();

        var suggestion = Assert.Single(await Suggestions());
        Assert.Equal("meeting", suggestion.GetProperty("kind").GetString());
        Assert.Equal("Taras", suggestion.GetProperty("name").GetString());
        Assert.Equal(0.6, suggestion.GetProperty("similarity").GetDouble(), 3);
        Assert.Equal("uid-1", suggestion.GetProperty("evidence").GetProperty("uid").GetString());
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from speaker_tracks where person_id is not null"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from track_suggestions where status <> 'pending'"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from people"));
    }

    [Fact]
    public async Task Calendar_names_are_off_by_default()
    {
        Boot();
        var c = await Conversation();
        await Line(c, 0, 30, 0, user: true);
        await Line(c, 30, 40, 1);
        await Meeting("Taras");

        await Build();

        Assert.Empty(await Suggestions());
    }

    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public int Dimensions => Size;

        public float[] Embed(ReadOnlySpan<float> samples) => throw new InvalidOperationException("No transcription runs here.");
    }
}
