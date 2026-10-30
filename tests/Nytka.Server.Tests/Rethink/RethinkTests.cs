using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Audio.Voice;
using Nytka.Server.Jobs;
using Nytka.Server.Rethink;
using Nytka.Server.Settings;
using Nytka.Server.Situations;
using Nytka.Server.Voice;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Rethink;

/// <summary>
/// The nightly re-think end to end (nytka-app/server#306) on a real database: a derived row records the version that made it, a changed
/// setting makes the days stale for the layer and the layers after it, and the quiet-hours job rebuilds them within its caps.
/// </summary>
public sealed class RethinkTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private const string Model = "fake-model";
    private const int Size = 192;

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(StartMs);

    /// <summary>The first quiet hour of the night after the days the tests build.</summary>
    private static readonly DateTimeOffset Quiet = new(Start.Year, Start.Month, Start.Day, 23, 0, 0, TimeSpan.Zero);

    private NytkaApiFactory? _server;

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server?.Dispose();
        return Task.CompletedTask;
    }

    private NytkaApiFactory Boot(Action<IDictionary<string, string?>>? configure = null, Action<IServiceCollection>? services = null)
    {
        _server?.Dispose();
        _server = new NytkaApiFactory(
            db,
            settings =>
            {
                settings["Nytka:Tracks:Enabled"] = "true";
                configure?.Invoke(settings);
            },
            s =>
            {
                s.AddSingleton(new SpeakerModel(new FakeEmbedder(), Model));
                services?.Invoke(s);
            });
        return _server;
    }

    private HttpClient Client => _server!.CreateAuthorizedClient();

    private static float[] Voice(int axis)
    {
        var vector = new float[Size];
        vector[axis] = 1;
        vector[100 + axis] = 0.1f;
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        return vector.Select(v => v / norm).ToArray();
    }

    /// <summary>A closed conversation of two voices at <paramref name="at"/>, three lines each: the first lines are one voice's, the second's another's.</summary>
    private async Task<long[]> Day(DateTimeOffset at)
    {
        var conversation = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@conversation, @at, @at + interval '2 minutes', 'closed', @at, @at)",
            new { conversation, at });
        var batch = await db.ScalarAsync<long>(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@conversation, @at, @at, 'done', '{}', @at) returning id",
            new { conversation, at });
        var lines = new List<long>();
        for (var i = 0; i < 6; i++)
        {
            var from = at.AddSeconds(i * 10);
            var id = await db.ScalarAsync<long>(
                "insert into segments (conversation_id, batch_id, started_at, ended_at, text) values (@conversation, @batch, @from, @until, 'we talked about the plan') returning id",
                new { conversation, batch, from, until = from.AddSeconds(8) });
            await db.ExecuteAsync(
                "insert into track_prints (segment_id, idx, batch_id, model, fingerprint, created_at) values (@id, 0, @batch, @Model, @print, now())",
                new { id, batch, Model, print = VoiceStore.Encode(Voice(i % 2)) });
            lines.Add(id);
        }

        return [.. lines];
    }

    private async Task Tick()
    {
        await _server!.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    /// <summary>The next day has come: tracks are built, then named, then the day is cut.</summary>
    private async Task BuildDays()
    {
        _server!.Time.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < 3; i++)
        {
            await Tick();
        }
    }

    private async Task<Dictionary<string, JsonElement>> Derivations() =>
        (await Client.GetFromJsonAsync<JsonElement>("/api/v1/quality")).GetProperty("derivations").EnumerateArray()
        .ToDictionary(d => d.GetProperty("layer").GetString()!);

    private static int Stale(Dictionary<string, JsonElement> layers, string layer) => layers[layer].GetProperty("staleDays").GetInt32();

    private static long Version(Dictionary<string, JsonElement> layers, string layer) => layers[layer].GetProperty("version").GetInt64();

    [Fact]
    public async Task Changing_the_tracks_threshold_makes_every_built_day_stale_for_tracks_and_the_layers_after_it()
    {
        Boot(s => s["Nytka:Situations:Enabled"] = "true");
        await Day(Start);
        await Day(Start.AddDays(-1));
        await BuildDays();

        var before = await Derivations();
        Assert.Equal([0, 0, 0], new[] { "tracks", "names", "situations" }.Select(l => Stale(before, l)));
        Assert.Equal(2, await db.ScalarAsync<long>("select count(distinct day) from speaker_tracks where derivation_id is not null"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from speaker_tracks where derivation_id is null"));
        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from track_name_runs where derivation_id is not null"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from situations where derivation_id is null"));

        Boot(s =>
        {
            s["Nytka:Situations:Enabled"] = "true";
            s["Nytka:Tracks:Threshold"] = "0.6";
        });
        var after = await Derivations();

        Assert.Equal([2, 2, 2], new[] { "tracks", "names", "situations" }.Select(l => Stale(after, l)));
        Assert.All(new[] { "tracks", "names", "situations" }, l => Assert.True(Version(after, l) > Version(before, l), l));
        // Neither layer follows the tracks, and both are off: no model is set.
        Assert.Equal([0, 0], new[] { "links", "enrichment" }.Select(l => Stale(after, l)));
        Assert.All(new[] { "links", "enrichment" }, l => Assert.Equal(Version(before, l), Version(after, l)));
    }

    [Fact]
    public async Task A_manual_same_answer_survives_a_rebuild()
    {
        Boot();
        var lines = await Day(Start);
        await BuildDays();
        Assert.NotEqual(await TrackOf(lines[0]), await TrackOf(lines[1]));
        var client = Client;
        Assert.True((await client.PostAsJsonAsync("/api/v1/tracks/answers", new { segmentId = lines[0], otherSegmentId = lines[1], answer = "same" })).IsSuccessStatusCode);
        await client.PostAsJsonAsync("/api/v1/tracks/build", new { day = DayText });
        await _server!.RunJobsAsync();
        Assert.Equal(await TrackOf(lines[0]), await TrackOf(lines[1]));
        var oldVersion = await db.ScalarAsync<long>("select min(derivation_id) from speaker_tracks");

        Boot(s =>
        {
            s["Nytka:Tracks:Threshold"] = "0.6";
            s["Nytka:Rethink:Enabled"] = "true";
        });
        _server!.Time.SetUtcNow(Quiet.AddDays(1));
        await Tick();

        Assert.True(await db.ScalarAsync<long>("select min(derivation_id) from speaker_tracks") > oldVersion);
        Assert.Equal(0, Stale(await Derivations(), "tracks"));
        Assert.Equal(await TrackOf(lines[0]), await TrackOf(lines[1]));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from track_answers where answer = 'same'"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from rethink_days where layer = 'tracks'"));
    }

    [Fact]
    public async Task The_nightly_cap_stops_the_job_when_it_is_reached()
    {
        // 1 character a token at $1,000 per million tokens: a character costs $0.001 in or out, a day of one situation about $2.3.
        const string Prices = """{"m2":{"textCharsPerToken":1,"textInUsdPerMillionTokens":1000,"textOutUsdPerMillionTokens":1000}}""";
        await db.ExecuteAsync(
            "insert into derivation_versions (layer, model_hash, prompt_hash, settings_hash, upstream_hash, created_at) values ('enrichment', 'x', 'x', 'x', 'x', now())");
        var old = await db.ScalarAsync<long>("select id from derivation_versions");
        foreach (var offset in new[] { -3, -2, -1 })
        {
            await Situation(Start.AddDays(offset), old);
        }

        Boot(s =>
        {
            s["Nytka:Situations:Enabled"] = "true";
            s["Nytka:Llm:BaseUrl"] = "http://llm.test/v1";
            s["Nytka:Llm:Model"] = "m2";
            s["Nytka:Stt:Prices"] = Prices;
            s["Nytka:Rethink:Enabled"] = "true";
            s["Nytka:Rethink:MaxUsdPerNight"] = "5.5";
        }, services => services.AddSingleton<Nytka.Server.Ai.ILlmClient>(new FakeLlm { Respond = _ => """{"title":"New","summary":"Fresh."}""" }));
        _server!.Time.SetUtcNow(Quiet.AddDays(1));
        await Tick();

        var days = await db.QueryAsync<(string Layer, DateOnly Day, decimal Usd)>("select layer, day, usd from rethink_days order by day");
        Assert.Equal(2, days.Count);
        Assert.All(days, d => Assert.Equal("enrichment", d.Layer));
        Assert.Equal([DateOnly.FromDateTime(Start.UtcDateTime).AddDays(-3), DateOnly.FromDateTime(Start.UtcDateTime).AddDays(-2)], days.Select(d => d.Day));
        Assert.InRange(days.Sum(d => d.Usd), 4m, 5.5m);
        Assert.Equal("capped", await db.ScalarAsync<string>("select outcome from rethink_nights"));
        // The two queued summaries are made again by the current version; the third day waits for another night.
        Assert.Equal(1, Stale(await Derivations(), "enrichment"));
        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from situations where title = 'New'"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from situations where title = 'Old'"));
    }

    [Fact]
    public async Task A_day_with_a_conversation_that_is_still_open_waits_and_the_night_settles()
    {
        await db.ExecuteAsync(
            "insert into derivation_versions (layer, model_hash, prompt_hash, settings_hash, upstream_hash, created_at) values ('enrichment', 'x', 'x', 'x', 'x', now())");
        await Situation(Start.AddDays(-1), await db.ScalarAsync<long>("select id from derivation_versions"));
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@id, @at, @until, 'open', @at, @at)",
            new { id = Guid.NewGuid(), at = Start.AddDays(-1).AddHours(1), until = Quiet.AddDays(1) });

        Boot(s =>
        {
            s["Nytka:Situations:Enabled"] = "true";
            s["Nytka:Llm:BaseUrl"] = "http://llm.test/v1";
            s["Nytka:Llm:Model"] = "m2";
            s["Nytka:Rethink:Enabled"] = "true";
        }, services => services.AddSingleton<Nytka.Server.Ai.ILlmClient>(new FakeLlm()));
        _server!.Time.SetUtcNow(Quiet.AddDays(1));
        await Tick();

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from rethink_days"));
        Assert.Equal("settled", await db.ScalarAsync<string>("select outcome from rethink_nights"));
        Assert.Equal(1, Stale(await Derivations(), "enrichment"));
    }

    [Fact]
    public async Task Nothing_is_rebuilt_while_the_job_is_off_or_outside_the_quiet_hours()
    {
        Boot();
        await Day(Start);
        await BuildDays();
        Boot(s => s["Nytka:Tracks:Threshold"] = "0.6");
        _server!.Time.SetUtcNow(Quiet.AddDays(1));
        await Tick();
        Assert.Equal(1, Stale(await Derivations(), "tracks"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'rethink'"));

        Boot(s =>
        {
            s["Nytka:Tracks:Threshold"] = "0.6";
            s["Nytka:Rethink:Enabled"] = "true";
        });
        _server!.Time.SetUtcNow(Quiet.AddDays(1).AddHours(-11));
        await Tick();
        Assert.Equal(1, Stale(await Derivations(), "tracks"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'rethink'"));
    }

    [Fact]
    public async Task The_night_is_the_date_the_quiet_hours_began_on_and_the_default_cap_is_the_issues_number()
    {
        Boot();
        var settings = _server!.Get<SettingsService>();
        var zone = TimeZoneInfo.Utc;

        Assert.Equal(new DateOnly(2026, 9, 29), RethinkSettings.NightOf(settings, new DateTimeOffset(2026, 9, 29, 23, 0, 0, TimeSpan.Zero), zone));
        Assert.Equal(new DateOnly(2026, 9, 29), RethinkSettings.NightOf(settings, new DateTimeOffset(2026, 9, 30, 3, 0, 0, TimeSpan.Zero), zone));
        Assert.Null(RethinkSettings.NightOf(settings, new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero), zone));
        Assert.Equal(0.50m, RethinkSettings.MaxUsd(settings));
        Assert.False(RethinkSettings.Enabled(settings));
    }

    [Fact]
    public async Task A_layer_registered_later_takes_its_place_after_the_ones_it_follows()
    {
        Boot(services: s => s.AddSingleton<IDerivedLayer>(new LaterLayer()));

        var layers = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/quality")).GetProperty("derivations").EnumerateArray()
            .Select(d => d.GetProperty("layer").GetString()).ToList();

        Assert.True(layers.IndexOf("later") > layers.IndexOf("situations"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from derivation_versions where layer = 'later'"));
    }

    private string DayText => DateOnly.FromDateTime(Start.UtcDateTime).ToString("yyyy-MM-dd");

    private Task<Guid?> TrackOf(long segment) => db.ScalarAsync<Guid?>("select track_id from segments where id = @segment", new { segment });

    /// <summary>A closed day with one situation whose summary the version <paramref name="version"/> made.</summary>
    private async Task Situation(DateTimeOffset at, long version)
    {
        var conversation = Guid.NewGuid();
        var situation = Guid.NewGuid();
        var batch = await db.ScalarAsync<long>(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@conversation, @at, @at + interval '2 minutes', 'closed', @at, @at);
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@conversation, @at, @at, 'done', '{}', @at) returning id
            """,
            new { conversation, at });
        var segment = await db.ScalarAsync<long>(
            "insert into segments (conversation_id, batch_id, started_at, ended_at, text) values (@conversation, @batch, @at, @at + interval '8 seconds', 'we talked about the plan for the week and the trip and what to buy and who calls whom today and tomorrow') returning id",
            new { conversation, batch, at });
        await db.ExecuteAsync(
            """
            insert into situations (id, day, conversation_id, started_at, ended_at, starts_with, track_ids, activity, media_share, noise, title, summary,
                                    version, lines_hash, signature, ai_status, ai_lines_hash, ai_derivation_id, built_at)
            values (@situation, @day, @conversation, @at, @at + interval '2 minutes', 'conversation_started', '{}', 'conversation', 0, false, 'Old', 'Old summary',
                    1, 'h', 's', 'done', 'h', @version, @at);
            insert into situation_segments (situation_id, segment_id) values (@situation, @segment);
            insert into situation_days (day, stage, built_at) values (@day, 'final', @at)
            """,
            new { situation, conversation, segment, at, day = DateOnly.FromDateTime(at.UtcDateTime), version });
    }

    /// <summary>What facts (nytka-app/server#305) will be: a layer of its own after situations, added by registering it.</summary>
    private sealed class LaterLayer : IDerivedLayer
    {
        public string Name => "later";

        public IReadOnlyList<string> DependsOn => [DerivedLayers.Situations];

        public bool Enabled => true;

        public bool CallsModel => true;

        public LayerInputs Inputs() => new("m", "p", new Dictionary<string, string?>());

        public Task<IReadOnlyList<DateOnly>> StaleDaysAsync(LayerVersion version, CancellationToken ct) => Task.FromResult<IReadOnlyList<DateOnly>>([]);

        public Task<LayerChars> EstimateAsync(DateOnly day, LayerVersion version, CancellationToken ct) => Task.FromResult(default(LayerChars));

        public Task RebuildAsync(DateOnly day, LayerVersion version, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public int Dimensions => Size;

        public float[] Embed(ReadOnlySpan<float> samples) => throw new InvalidOperationException("No transcription runs here.");
    }
}
