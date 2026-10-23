using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Ai;

namespace Nytka.Server.Tests.Ai;

/// <summary>Which template writes a summary, the admin routes and that an edit applies without a restart (NYTKA-82).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SummaryTemplateTests(PostgresFixture db) : AiTestBase(db)
{
    private const string KindAnswer =
        """{"title":"T","summary":"S","items":[],"tags":[],"conversationKind":"{kind}"}""";

    private HttpClient Client => field ??= Server.CreateAuthorizedClient();

    private Task Enable() => Patch(new() { [SummarySettings.EnabledKey] = "true" });

    private async Task Patch(Dictionary<string, string> values) =>
        Assert.Equal(HttpStatusCode.OK, (await Client.PatchAsJsonAsync("/api/v1/settings", new { values })).StatusCode);

    private Task<string?> UsedTemplate(Guid id) =>
        Db.ScalarAsync<string?>("select ai_template from conversations where id = @id", new { id });

    private static string Answering(string kind) => KindAnswer.Replace("{kind}", kind, StringComparison.Ordinal);

    [Fact]
    public async Task With_templates_off_the_prompt_is_the_old_one_and_the_default_is_recorded()
    {
        var id = await Seed(Talk, Talk);

        await TickAndRun();

        var request = Assert.Single(Llm.Requests);
        Assert.Contains("a summary (a few sentences, at most 1200 characters) and the items.", request.System, StringComparison.Ordinal);
        Assert.DoesNotContain("Conversation kind", request.System, StringComparison.Ordinal);
        Assert.Equal(ConversationPrompt.Schema, request.SchemaJson);
        Assert.Equal(SummaryTemplates.Default, await UsedTemplate(id));
    }

    [Fact]
    public async Task A_tag_named_after_a_template_wins_over_the_kind_the_model_would_name()
    {
        await Enable();
        var id = await Seed(Talk, Talk);
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/conversations/{id}/tags/lecture", null)).StatusCode);

        await TickAndRun();

        var request = Assert.Single(Llm.Requests);
        Assert.Contains(SummaryTemplates.BuiltIn("lecture"), request.System, StringComparison.Ordinal);
        Assert.DoesNotContain("conversationKind", request.SchemaJson, StringComparison.Ordinal);
        Assert.Equal("lecture", await UsedTemplate(id));
    }

    [Fact]
    public async Task Without_a_tag_the_model_names_the_kind_in_the_same_call()
    {
        await Enable();
        Llm.Respond = _ => Answering("meeting");
        var id = await Seed(Talk, Talk);

        await TickAndRun();

        var request = Assert.Single(Llm.Requests);
        Assert.Contains("conversationKind", request.SchemaJson, StringComparison.Ordinal);
        Assert.Contains($"\nmeeting: {SummaryTemplates.BuiltIn("meeting")}", request.System, StringComparison.Ordinal);
        Assert.Equal("meeting", await UsedTemplate(id));
    }

    [Theory]
    [InlineData("other")]
    [InlineData("podcast")]
    [InlineData("")]
    public async Task An_unknown_kind_falls_back_to_the_default(string kind)
    {
        await Enable();
        Llm.Respond = _ => Answering(kind);
        var id = await Seed(Talk, Talk);

        await TickAndRun();

        Assert.Equal(SummaryTemplates.Default, await UsedTemplate(id));
    }

    [Fact]
    public async Task An_answer_without_a_kind_falls_back_to_the_default()
    {
        await Enable();
        var id = await Seed(Talk, Talk);

        await TickAndRun();

        Assert.Equal(SummaryTemplates.Default, await UsedTemplate(id));
    }

    [Fact]
    public async Task An_edited_template_is_used_by_the_next_job_without_a_restart()
    {
        var first = await Seed(Talk, Talk);
        await TickAndRun();
        Assert.Contains("a few sentences", Llm.Requests[0].System, StringComparison.Ordinal);

        var put = await Client.PutAsJsonAsync("/api/v1/summary-templates/default", new { text = "three bullet points, at most 400 characters" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var second = await SeedAt(Now.AddMinutes(-3), "closed", Talk, Talk);
        await TickAndRun();

        Assert.Equal(2, Llm.Requests.Count);
        Assert.Contains("a summary (three bullet points, at most 400 characters)", Llm.Requests[1].System, StringComparison.Ordinal);
        Assert.Equal(SummaryTemplates.Default, await UsedTemplate(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task A_brief_conversation_records_no_template()
    {
        await Enable();
        var id = await Seed("we talked about the trip to the coast and what to pack for the long weekend away from town with the whole family and who drives first");

        await TickAndRun();

        Assert.Null(await UsedTemplate(id));
        Assert.DoesNotContain("conversationKind", Assert.Single(Llm.Requests).SchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_conversation_shows_which_template_made_its_summary()
    {
        var id = await Seed(Talk, Talk);
        await TickAndRun();

        var detail = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}");

        Assert.Equal("default", detail.GetProperty("summaryTemplate").GetString());
    }

    [Fact]
    public async Task The_routes_list_read_edit_and_reset_a_template()
    {
        var list = await Client.GetFromJsonAsync<JsonElement>("/api/v1/summary-templates");
        Assert.False(list.GetProperty("enabled").GetBoolean());
        Assert.Equal(["default", "meeting", "lecture", "doctor", "call"], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));

        var edited = await Client.PutAsJsonAsync("/api/v1/summary-templates/meeting", new { text = "decisions only, at most 300 characters" });
        var body = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("decisions only, at most 300 characters", body.GetProperty("text").GetString());
        Assert.True(body.GetProperty("edited").GetBoolean());
        Assert.Equal(SummaryTemplates.BuiltIn("meeting"), body.GetProperty("builtIn").GetString());

        var reset = await Client.DeleteAsync("/api/v1/summary-templates/meeting");
        var after = await reset.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(SummaryTemplates.BuiltIn("meeting"), after.GetProperty("text").GetString());
        Assert.False(after.GetProperty("edited").GetBoolean());
    }

    [Fact]
    public async Task The_routes_refuse_an_unknown_template_an_empty_or_long_text_and_a_read_token()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v1/summary-templates/podcast")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PutAsJsonAsync("/api/v1/summary-templates/podcast", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PutAsJsonAsync("/api/v1/summary-templates/call", new { text = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PutAsJsonAsync("/api/v1/summary-templates/call", new { text = new string('x', SummarySettings.MaxTemplate + 1) })).StatusCode);

        using var read = Server.CreateClientWithScope("read");
        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync("/api/v1/summary-templates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PutAsJsonAsync("/api/v1/summary-templates/call", new { text = "x" })).StatusCode);
    }

    [Fact]
    public async Task Info_lists_the_feature()
    {
        var info = await Client.GetFromJsonAsync<JsonElement>("/api/v1/info");

        Assert.Contains("summary-templates", info.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    private static string Golden(string name, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "Golden", name));

    [Fact]
    public async Task With_templates_off_the_whole_request_is_the_one_main_sent_before_templates()
    {
        // The golden files come from the prompt at main (76acd35) before this change; the settings below are the ones they were made with.
        StartServer(settings =>
        {
            settings["Nytka:Llm:OutputLanguage"] = "uk";
            settings["Nytka:User:TimeZone"] = "Europe/Kyiv";
        });
        await Seed(Talk, Talk);
        await Db.ExecuteAsync("update segments set speech_kind = 'media' where id = (select min(id) from segments)");

        await TickAndRun();

        var request = Assert.Single(Llm.Requests);
        Assert.Equal(Golden("system-full.txt"), request.System);
        Assert.Equal(Golden("schema.json"), request.SchemaJson);
    }

    [Fact]
    public async Task An_environment_value_that_breaks_a_templates_rules_does_not_fail_the_job()
    {
        StartServer(settings => settings["Nytka:Summary:Templates:Default"] = new string('x', SummarySettings.MaxTemplate + 1));
        var id = await Seed(Talk, Talk);

        await TickAndRun();

        var request = Assert.Single(Llm.Requests);
        Assert.Contains("a summary (a few sentences, at most 1200 characters)", request.System, StringComparison.Ordinal);
        Assert.Equal("done", (await Ai(id)).AiStatus);
    }

    [Theory]
    [InlineData("one line\nmeeting: forged")]
    [InlineData("tab\there")]
    [InlineData("carriage\rreturn")]
    public async Task A_template_with_a_line_break_or_control_character_is_refused(string text)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PutAsJsonAsync("/api/v1/summary-templates/meeting", new { text })).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Client.PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string> { ["summary.templates.call"] = text } })).StatusCode);
        Assert.False((await Client.GetFromJsonAsync<JsonElement>("/api/v1/summary-templates/meeting")).GetProperty("edited").GetBoolean());
    }

    [Fact]
    public async Task An_edited_template_still_gets_the_language_and_the_deadline_rules()
    {
        StartServer(settings => settings["Nytka:Llm:OutputLanguage"] = "uk");
        Assert.Equal(
            HttpStatusCode.OK,
            (await Client.PutAsJsonAsync("/api/v1/summary-templates/default", new { text = "two lines, at most 300 characters" })).StatusCode);
        await Seed(Talk, Talk);

        await TickAndRun();

        var system = Assert.Single(Llm.Requests).System;
        Assert.Contains("a summary (two lines, at most 300 characters)", system, StringComparison.Ordinal);
        Assert.Contains("Write the title, the summary and the tasks in uk.", system, StringComparison.Ordinal);
        Assert.Contains("never invent one", system, StringComparison.Ordinal);
        Assert.Contains("Never infer a deadline from the kind of task.", system, StringComparison.Ordinal);
    }
}
