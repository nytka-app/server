using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nytka.Server.Settings;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Settings;

[Collection(PostgresCollection.Name)]
public sealed class SettingsApiTests(PostgresFixture db) : IAsyncLifetime
{
    private NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private static string Body(object values) => JsonSerializer.Serialize(new { values });

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static JsonElement Item(JsonElement list, string key) =>
        list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);

    private static Task<HttpResponseMessage> Patch(HttpClient client, object values) =>
        client.PatchAsync("/api/v1/settings", new StringContent(Body(values), System.Text.Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> Patch(object values) => Patch(_server.CreateAuthorizedClient(), values);

    private async Task<JsonElement> List(NytkaApiFactory? server = null) =>
        await Json(await (server ?? _server).CreateAuthorizedClient().GetAsync("/api/v1/settings"));

    private NytkaApiFactory ServerWithEnvironment(params (string Key, string Value)[] environment) =>
        new(db, settings =>
        {
            foreach (var (key, value) in environment)
            {
                settings[key] = value;
            }
        });

    [Fact]
    public async Task Get_lists_the_catalog_with_every_field()
    {
        var items = (await List()).GetProperty("items");

        Assert.Equal(
            [
                "stt.url", "stt.apiKey", "stt.model", "stt.language", "conversations.gap", "user.timeZone", "mute.windows", "audio.retentionDays",
                "llm.baseUrl", "llm.apiKey", "llm.model", "llm.outputLanguage",
                "memories.enabled", "memories.userName", "search.dictionary", "digest.enabled", "digest.hour",
                "voice.enabled", "voice.userThreshold", "voice.learnThreshold", "voice.learn", "voice.minSegmentSeconds",
                "people.suggestNames",
                "people.voiceMatching", "people.voiceThreshold",
            ],
            items.EnumerateArray().Select(i => i.GetProperty("key").GetString()));
        Assert.All(
            items.EnumerateArray(),
            item => Assert.Equal(
                ["key", "type", "value", "isSet", "source", "locked", "default"], item.EnumerateObject().Select(p => p.Name)));
    }

    [Theory]
    [InlineData("0.38", true)]
    [InlineData("0.95", true)]
    [InlineData("0.05", false)]
    [InlineData("0,4", false)]
    [InlineData("NaN", false)]
    public async Task The_voice_threshold_is_a_number_in_range(string value, bool accepted)
    {
        var response = await Patch(new Dictionary<string, string> { ["voice.userThreshold"] = value, ["voice.learnThreshold"] = "0.95" });

        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("number", Item(await List(), "voice.userThreshold").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("voice.learnThreshold", "0.3")]
    [InlineData("voice.userThreshold", "0.6")]
    public async Task The_learn_threshold_never_goes_below_the_user_threshold(string key, string value)
    {
        var response = await Patch(new Dictionary<string, string> { [key] = value });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([key], (await Json(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Equal("default", Item(await List(), key).GetProperty("source").GetString());
    }

    [Fact]
    public async Task Both_thresholds_may_move_together()
    {
        var response = await Patch(new Dictionary<string, string> { ["voice.userThreshold"] = "0.6", ["voice.learnThreshold"] = "0.6" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_threshold_conflict_the_environment_set_blocks_no_other_change()
    {
        using var server = ServerWithEnvironment(("Nytka:Voice:UserThreshold", "0.6"));

        var response = await Patch(server.CreateAuthorizedClient(), new Dictionary<string, string> { ["voice.learn"] = "false" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unset_key_shows_its_default()
    {
        var list = await List();

        var retention = Item(list, "audio.retentionDays");
        Assert.Equal("int", retention.GetProperty("type").GetString());
        Assert.Equal("14", retention.GetProperty("value").GetString());
        Assert.Equal("14", retention.GetProperty("default").GetString());
        Assert.Equal("default", retention.GetProperty("source").GetString());
        Assert.False(retention.GetProperty("isSet").GetBoolean());
        Assert.False(retention.GetProperty("locked").GetBoolean());
        Assert.Equal("00:02:00", Item(list, "conversations.gap").GetProperty("value").GetString());
        Assert.Equal("auto", Item(list, "stt.language").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, Item(list, "stt.model").GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task The_stt_url_is_always_locked_to_the_environment()
    {
        var url = Item(await List(), "stt.url");

        Assert.Equal("url", url.GetProperty("type").GetString());
        Assert.Equal("http://stt.test/v1/audio/transcriptions", url.GetProperty("value").GetString());
        Assert.Equal("env", url.GetProperty("source").GetString());
        Assert.True(url.GetProperty("locked").GetBoolean());
        Assert.True(url.GetProperty("isSet").GetBoolean());
    }

    [Fact]
    public async Task An_api_key_shows_only_whether_it_is_set()
    {
        const string configured = "abc123-not-a-real-value";
        using var with = ServerWithEnvironment(("Nytka:Stt:ApiKey", configured));

        var unset = Item(await List(), "stt.apiKey");
        var response = await with.CreateAuthorizedClient().GetAsync("/api/v1/settings");
        var body = await response.Content.ReadAsStringAsync();
        var set = Item(JsonDocument.Parse(body).RootElement, "stt.apiKey");

        Assert.Equal("secret", unset.GetProperty("type").GetString());
        Assert.False(unset.GetProperty("isSet").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unset.GetProperty("value").ValueKind);
        Assert.True(unset.GetProperty("locked").GetBoolean());
        Assert.True(set.GetProperty("isSet").GetBoolean());
        Assert.Equal(JsonValueKind.Null, set.GetProperty("value").ValueKind);
        Assert.Equal("env", set.GetProperty("source").GetString());
        Assert.True(set.GetProperty("locked").GetBoolean());
        Assert.DoesNotContain(configured, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patch_writes_a_row_answers_the_new_list_and_applies_at_once()
    {
        var response = await Patch(new Dictionary<string, string?> { ["audio.retentionDays"] = "30", ["stt.model"] = "whisper-large" });
        var list = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var retention = Item(list, "audio.retentionDays");
        Assert.Equal("30", retention.GetProperty("value").GetString());
        Assert.Equal("db", retention.GetProperty("source").GetString());
        Assert.True(retention.GetProperty("isSet").GetBoolean());
        Assert.False(retention.GetProperty("locked").GetBoolean());
        Assert.Equal("14", retention.GetProperty("default").GetString());
        Assert.Equal(list.ToString(), (await List()).ToString());
        Assert.Equal(
            ["30", "whisper-large"],
            await db.QueryAsync<string>("select value from settings order by key"));
        Assert.Equal(30, _server.Get<IOptions<NytkaOptions>>().Value.Audio.RetentionDays);
        Assert.Equal(30, _server.Get<IOptionsMonitor<NytkaOptions>>().CurrentValue.Audio.RetentionDays);
    }

    [Fact]
    public async Task Patch_replaces_a_row_it_wrote_before()
    {
        await Patch(new Dictionary<string, string?> { ["conversations.gap"] = "00:05:00" });

        var response = await Patch(new Dictionary<string, string?> { ["conversations.gap"] = "00:10:00" });

        Assert.Equal("00:10:00", Item(await Json(response), "conversations.gap").GetProperty("value").GetString());
        Assert.Equal(1, await db.ScalarAsync<int>("select count(*)::int from settings"));
        Assert.Equal(TimeSpan.FromMinutes(10), _server.Get<IOptions<NytkaOptions>>().Value.Conversations.Gap);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Patch_with_null_or_an_empty_value_removes_the_row_and_restores_the_default(string? value)
    {
        await Patch(new Dictionary<string, string?> { ["audio.retentionDays"] = "30" });

        var response = await Patch(new Dictionary<string, string?> { ["audio.retentionDays"] = value });

        var retention = Item(await Json(response), "audio.retentionDays");
        Assert.Equal("14", retention.GetProperty("value").GetString());
        Assert.Equal("default", retention.GetProperty("source").GetString());
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from settings"));
        Assert.Equal(14, _server.Get<IOptions<NytkaOptions>>().Value.Audio.RetentionDays);
    }

    [Fact]
    public async Task Patch_of_an_empty_object_changes_nothing()
    {
        var response = await Patch(new Dictionary<string, string?>());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("audio.retentionDays", "-1")]
    [InlineData("audio.retentionDays", "3651")]
    [InlineData("audio.retentionDays", "many")]
    [InlineData("audio.retentionDays", "1.5")]
    [InlineData("conversations.gap", "00:00:29")]
    [InlineData("conversations.gap", "01:00:01")]
    [InlineData("conversations.gap", "2 minutes")]
    [InlineData("conversations.gap", "120")]
    [InlineData("stt.language", "English")]
    [InlineData("stt.language", "en_GB")]
    [InlineData("stt.model", "m-129-characters-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task A_bad_value_is_a_400_naming_the_key_and_writes_nothing(string key, string value)
    {
        var response = await Patch(new Dictionary<string, string?> { [key] = value });
        var problem = await Json(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(problem.GetProperty("errors").GetProperty(key).EnumerateArray());
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from settings"));
    }

    [Theory]
    [InlineData("audio.retentionDays", "0")]
    [InlineData("audio.retentionDays", "3650")]
    [InlineData("conversations.gap", "00:00:30")]
    [InlineData("conversations.gap", "01:00:00")]
    [InlineData("stt.language", "auto")]
    [InlineData("stt.language", "uk")]
    [InlineData("stt.language", "en-GB")]
    [InlineData("stt.language", "zh-Hant-TW")]
    public async Task The_edges_of_the_rules_are_accepted(string key, string value)
    {
        var response = await Patch(new Dictionary<string, string?> { [key] = value });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_name_of_128_characters_is_allowed()
    {
        var response = await Patch(new Dictionary<string, string?> { ["stt.model"] = new string('m', 128) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_key_is_a_400()
    {
        var response = await Patch(new Dictionary<string, string?> { ["audio.nothing"] = "1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await Json(response)).GetProperty("errors").GetProperty("audio.nothing").EnumerateArray());
    }

    [Fact]
    public async Task Patch_is_all_or_nothing()
    {
        var response = await Patch(new Dictionary<string, string?> { ["audio.retentionDays"] = "30", ["conversations.gap"] = "soon" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from settings"));
        Assert.Equal(14, _server.Get<IOptions<NytkaOptions>>().Value.Audio.RetentionDays);
    }

    [Fact]
    public async Task A_key_the_environment_supplies_is_locked_and_refuses_writes_with_409()
    {
        using var server = ServerWithEnvironment(("Nytka:Audio:RetentionDays", "30"));

        var retention = Item(await List(server), "audio.retentionDays");
        var response = await Patch(server.CreateAuthorizedClient(), new Dictionary<string, string?> { ["audio.retentionDays"] = "7" });
        var cleared = await Patch(server.CreateAuthorizedClient(), new Dictionary<string, string?> { ["audio.retentionDays"] = null });

        Assert.Equal("env", retention.GetProperty("source").GetString());
        Assert.True(retention.GetProperty("locked").GetBoolean());
        Assert.Equal("30", retention.GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("audio.retentionDays", (await Json(response)).GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, cleared.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from settings"));
        Assert.Equal(30, server.Get<IOptions<NytkaOptions>>().Value.Audio.RetentionDays);
    }

    [Theory]
    [InlineData("stt.url", "http://elsewhere.test/")]
    [InlineData("stt.apiKey", "stt-key-new")]
    [InlineData("stt.apiKey", null)]
    [InlineData("stt.apiKey", "")]
    public async Task The_stt_url_and_any_api_key_refuse_writes_with_409(string key, string? value)
    {
        var response = await Patch(new Dictionary<string, string?> { [key] = value });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from settings"));
    }

    [Fact]
    public async Task One_locked_key_refuses_the_whole_patch()
    {
        var response = await Patch(new Dictionary<string, string?> { ["audio.retentionDays"] = "30", ["stt.apiKey"] = "stt-key-new" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from settings"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"values":null}""")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task A_body_without_values_is_a_400(string body)
    {
        var response = await _server.CreateAuthorizedClient().PatchAsync(
            "/api/v1/settings", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("14")]
    [InlineData("true")]
    [InlineData("[]")]
    public async Task A_value_that_is_not_a_string_or_null_is_a_400_naming_the_key(string json)
    {
        var response = await _server.CreateAuthorizedClient().PatchAsync(
            "/api/v1/settings",
            new StringContent("""{"values":{"audio.retentionDays":""" + json + "}}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty((await Json(response)).GetProperty("errors").GetProperty("audio.retentionDays").EnumerateArray());
    }

    [Fact]
    public async Task A_changed_setting_applies_to_the_next_job_without_a_restart()
    {
        var session = Guid.NewGuid();
        await _server.UploadAsync(Chunks(session, Tone(6), Silence(3)));
        await _server.RunJobsAsync();
        Assert.False(Assert.Single(_server.Stt.Requests).Fields.ContainsKey("model"));

        await Patch(new Dictionary<string, string?> { ["stt.model"] = "whisper-large", ["stt.language"] = "uk" });
        await _server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));
        await _server.RunJobsAsync();

        var next = _server.Stt.Requests[^1];
        Assert.Equal(2, _server.Stt.Requests.Count);
        Assert.Equal("whisper-large", next.Fields["model"]);
        Assert.Equal("uk", next.Fields["language"]);
    }

    [Fact]
    public async Task A_row_from_before_the_server_started_applies()
    {
        await db.ExecuteAsync(
            """
            insert into settings (key, value, updated_at)
            values ('audio.retentionDays', '3', now()), ('conversations.gap', '00:07:00', now())
            """);
        using var server = new NytkaApiFactory(db);

        var list = await List(server);

        Assert.Equal("3", Item(list, "audio.retentionDays").GetProperty("value").GetString());
        Assert.Equal(3, server.Get<IOptions<NytkaOptions>>().Value.Audio.RetentionDays);
        Assert.Equal(TimeSpan.FromMinutes(7), server.Get<IOptions<NytkaOptions>>().Value.Conversations.Gap);
    }

    [Fact]
    public async Task An_empty_environment_variable_counts_as_unset_and_never_shadows_the_row()
    {
        await db.ExecuteAsync("insert into settings (key, value, updated_at) values ('audio.retentionDays', '3', now())");
        using var server = ServerWithEnvironment(
            ("Nytka:Audio:RetentionDays", ""), ("Nytka:Conversations:Gap", ""), ("Nytka:Stt:Model", ""), ("Nytka:Stt:Language", ""));

        var list = await List(server);
        var options = server.Get<IOptions<NytkaOptions>>().Value;

        var retention = Item(list, "audio.retentionDays");
        Assert.Equal("db", retention.GetProperty("source").GetString());
        Assert.False(retention.GetProperty("locked").GetBoolean());
        Assert.Equal("default", Item(list, "conversations.gap").GetProperty("source").GetString());
        Assert.Equal(3, options.Audio.RetentionDays);
        Assert.Equal(TimeSpan.FromMinutes(2), options.Conversations.Gap);
        Assert.Null(options.Stt.Model);
        Assert.Equal("auto", options.Stt.Language);
    }

    [Fact]
    public async Task An_empty_variable_leaves_the_key_editable()
    {
        using var server = ServerWithEnvironment(("Nytka:Audio:RetentionDays", ""));

        var response = await Patch(server.CreateAuthorizedClient(), new Dictionary<string, string?> { ["audio.retentionDays"] = "9" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(9, server.Get<IOptions<NytkaOptions>>().Value.Audio.RetentionDays);
    }

    [Fact]
    public async Task The_environment_beats_the_row()
    {
        await db.ExecuteAsync("insert into settings (key, value, updated_at) values ('audio.retentionDays', '3', now())");
        using var server = ServerWithEnvironment(("Nytka:Audio:RetentionDays", "30"));

        var retention = Item(await List(server), "audio.retentionDays");

        Assert.Equal("30", retention.GetProperty("value").GetString());
        Assert.Equal("env", retention.GetProperty("source").GetString());
        Assert.Equal(30, server.Get<IOptions<NytkaOptions>>().Value.Audio.RetentionDays);
    }

    [Fact]
    public async Task A_row_that_breaks_its_key_or_belongs_to_no_key_is_ignored()
    {
        await db.ExecuteAsync(
            """
            insert into settings (key, value, updated_at)
            values ('audio.retentionDays', 'lots', now()), ('stt.apiKey', 'stt-key-from-table', now()),
                   ('stt.url', 'http://from-table.test/', now()), ('gone.key', 'x', now())
            """);
        using var server = new NytkaApiFactory(db);

        var list = await List(server);

        Assert.Equal("default", Item(list, "audio.retentionDays").GetProperty("source").GetString());
        Assert.False(Item(list, "stt.apiKey").GetProperty("isSet").GetBoolean());
        Assert.Equal("http://stt.test/v1/audio/transcriptions", Item(list, "stt.url").GetProperty("value").GetString());
        Assert.Null(server.Get<IOptions<NytkaOptions>>().Value.Stt.ApiKey);
    }

    [Fact]
    public async Task No_api_key_reaches_the_database()
    {
        const string configured = "never-stored-not-real";
        using var server = ServerWithEnvironment(("Nytka:Stt:ApiKey", configured));

        await Patch(server.CreateAuthorizedClient(), new Dictionary<string, string?> { ["stt.apiKey"] = "stt-key-attempt" });
        await Patch(server.CreateAuthorizedClient(), new Dictionary<string, string?> { ["audio.retentionDays"] = "30" });

        Assert.Equal(["audio.retentionDays"], await db.QueryAsync<string>("select key from settings"));
        Assert.Equal(configured, server.Get<IOptions<NytkaOptions>>().Value.Stt.ApiKey);
    }

    [Theory]
    [InlineData("Nytka:Conversations:Gap", "00:00:05", "Nytka__Conversations__Gap")]
    [InlineData("Nytka:Stt:Language", "English", "Nytka__Stt__Language")]
    [InlineData("Nytka:Audio:RetentionDays", "-3", "Nytka__Audio__RetentionDays")]
    public void The_server_refuses_to_start_on_an_environment_value_that_breaks_its_rules(string key, string value, string variable)
    {
        using var server = ServerWithEnvironment((key, value));

        var error = Assert.ThrowsAny<Exception>(() => server.CreateClient());

        Assert.Contains(variable, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_resolve_through_the_options_monitor_after_the_table_loads()
    {
        // Program.cs reads the options before the migrator, when the table is not loaded yet; that read must not stick.
        await db.ExecuteAsync("insert into settings (key, value, updated_at) values ('stt.model', 'from-table', now())");
        using var server = new NytkaApiFactory(db);

        _ = server.CreateClient();

        Assert.Equal("from-table", server.Get<IOptions<NytkaOptions>>().Value.Stt.Model);
        Assert.Equal("from-table", server.Get<IOptionsMonitor<NytkaOptions>>().CurrentValue.Stt.Model);
    }

    [Fact]
    public async Task The_options_binder_never_sees_an_empty_environment_value()
    {
        // Program.cs's order on a host of its own: sources first, then the layer, then the binding. A Compose
        // file passes every optional variable empty, and the binder fails on an empty number or duration.
        await db.ExecuteAsync("insert into settings (key, value, updated_at) values ('audio.retentionDays', '3', now())");
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nytka:AdminToken"] = NytkaApiFactory.Token,
            ["Nytka:Stt:Url"] = "http://stt.test/",
            ["Nytka:Stt:Model"] = "",
            ["Nytka:Conversations:Gap"] = "",
            ["Nytka:Audio:RetentionDays"] = "",
            ["Other:Key"] = "",
        });
        builder.AddNytkaSettingsLayer();
        builder.Services.AddOptions<NytkaOptions>().Bind(builder.Configuration.GetSection(NytkaOptions.Section));
        builder.Services.AddSingleton(db.DataSource);
        builder.Services.AddSingleton<SettingStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddNytkaSettings();
        using var host = builder.Build();
        await host.StartAsync();

        var options = host.Services.GetRequiredService<IOptions<NytkaOptions>>().Value;

        Assert.Equal(3, options.Audio.RetentionDays);
        Assert.Equal(TimeSpan.FromMinutes(2), options.Conversations.Gap);
        Assert.Null(options.Stt.Model);
        Assert.Equal("", host.Services.GetRequiredService<IConfiguration>()["Other:Key"]);
        await host.StopAsync();
    }

    [Fact]
    public async Task A_url_is_shown_without_its_credentials()
    {
        using var server = ServerWithEnvironment(("Nytka:Stt:Url", "https://user:hunter2@stt.test/v1/transcribe"));

        var response = await server.CreateAuthorizedClient().GetAsync("/api/v1/settings");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", body, StringComparison.Ordinal);
        Assert.Equal(
            "https://stt.test/v1/transcribe",
            Item(JsonDocument.Parse(body).RootElement, "stt.url").GetProperty("value").GetString());
    }
}
