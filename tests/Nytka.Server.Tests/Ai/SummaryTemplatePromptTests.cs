using System.Runtime.CompilerServices;
using System.Text.Json;
using Nytka.Server.Ai;

namespace Nytka.Server.Tests.Ai;

/// <summary>The default summary template reproduces the prompt as it was before templates existed (NYTKA-82).</summary>
public class SummaryTemplatePromptTests
{
    private static string Golden(string name, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "Golden", name));

    [Fact]
    public void The_default_template_gives_the_system_messages_and_schema_byte_for_byte()
    {
        Assert.Equal(Golden("system-full.txt"), ConversationPrompt.System("uk", "Europe/Kyiv", false, true, true));
        Assert.Equal(Golden("system-full.txt"), ConversationPrompt.System("uk", "Europe/Kyiv", false, true, true, SummaryTemplates.BuiltIn(SummaryTemplates.Default)));
        Assert.Equal(Golden("system-brief.txt"), ConversationPrompt.System("auto", "UTC", true, false, false));
        Assert.Equal(Golden("system-merge.txt"), ConversationPrompt.SystemForMerge("auto", "UTC", false, false));
        Assert.Equal(ConversationPrompt.Schema, Golden("schema.json"));
    }

    [Fact]
    public void A_template_replaces_only_the_summary_wording()
    {
        var plain = ConversationPrompt.System("uk", "Europe/Kyiv", false, true, true);
        var templated = ConversationPrompt.System("uk", "Europe/Kyiv", false, true, true, "the decisions, at most 1200 characters");

        Assert.Equal(plain.Replace("a few sentences, at most 1200 characters", "the decisions, at most 1200 characters", StringComparison.Ordinal), templated);
    }

    [Fact]
    public void Every_template_keeps_the_language_and_the_deadline_rules_and_the_items()
    {
        var kinds = SummaryTemplates.Ids.ToDictionary(id => id, SummaryTemplates.BuiltIn);
        foreach (var system in new[]
        {
            ConversationPrompt.System("uk", summarySpec: "anything"),
            ConversationPrompt.System("uk", kinds: kinds),
            ConversationPrompt.SystemForMerge("uk", kinds: kinds),
        })
        {
            Assert.Contains("Write the title, the summary and the tasks in uk.", system, StringComparison.Ordinal);
            Assert.Contains("never invent one", system, StringComparison.Ordinal);
            Assert.Contains("Never infer a deadline from the kind of task.", system, StringComparison.Ordinal);
            Assert.Contains("Kind \"commitment\"", system, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_kind_prompt_lists_every_kind_with_its_text_and_the_schema_asks_for_the_kind()
    {
        var kinds = SummaryTemplates.Ids.ToDictionary(id => id, id => $"text of {id}");

        var system = ConversationPrompt.System("auto", kinds: kinds);

        foreach (var id in SummaryTemplates.Kinds)
        {
            Assert.Contains($"\n{id}: text of {id}", system, StringComparison.Ordinal);
        }

        Assert.Contains("\nother: text of default", system, StringComparison.Ordinal);
        var schema = JsonSerializer.Deserialize<JsonElement>(ConversationPrompt.SchemaWithKind);
        Assert.Contains("conversationKind", schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(
            ["meeting", "lecture", "doctor", "call", "other"],
            schema.GetProperty("properties").GetProperty("conversationKind").GetProperty("enum").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public void A_brief_conversation_takes_no_kind_even_when_asked()
    {
        Assert.Equal(
            ConversationPrompt.System("auto", brief: true),
            ConversationPrompt.System("auto", brief: true, kinds: SummaryTemplates.Ids.ToDictionary(id => id, SummaryTemplates.BuiltIn)));
    }

    [Fact]
    public void Apart_from_the_summary_wording_the_kind_prompt_is_the_default_prompt()
    {
        var kinds = SummaryTemplates.Ids.ToDictionary(id => id, SummaryTemplates.BuiltIn);

        Assert.Equal(ConversationPrompt.System("uk", "Europe/Kyiv", false, true, true), WithoutKinds(ConversationPrompt.System("uk", "Europe/Kyiv", false, true, true, kinds: kinds)));
        Assert.Equal(ConversationPrompt.SystemForMerge("uk"), WithoutKinds(ConversationPrompt.SystemForMerge("uk", kinds: kinds), merge: true));
    }

    [Fact]
    public void Apart_from_the_summary_wording_an_edited_template_prompt_is_the_default_prompt()
    {
        Assert.Equal(
            ConversationPrompt.System("uk"),
            ConversationPrompt.System("uk", summarySpec: "zzz").Replace("(zzz)", "(" + ConversationPrompt.DefaultSummarySpec + ")", StringComparison.Ordinal));
    }

    /// <summary>Takes the kind block out of a kind-mode prompt and puts the default summary wording back.</summary>
    private static string WithoutKinds(string system, bool merge = false)
    {
        var start = system.IndexOf("\nConversation kind:", StringComparison.Ordinal);
        var end = system.IndexOf("\nItems:", start, StringComparison.Ordinal);
        var plain = system.Remove(start, end - start)
            .Replace(
                $"a summary (as the kind below says; never more than {ConversationPrompt.MaxSummary} characters)",
                $"a summary ({ConversationPrompt.DefaultSummarySpec})", StringComparison.Ordinal);
        return merge ? plain.Replace(" Set conversationKind to the kind that fits the whole conversation.", "", StringComparison.Ordinal) : plain;
    }
}
