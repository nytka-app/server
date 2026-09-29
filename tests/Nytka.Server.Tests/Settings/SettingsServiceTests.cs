using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Tests.Settings;

public class SettingsServiceTests
{
    private sealed class Group(params SettingDefinition[] definitions) : ISettingsGroup
    {
        public IReadOnlyList<SettingDefinition> Definitions { get; } = definitions;
    }

    private static SettingsService Service(IDictionary<string, string?> environment, params ISettingsGroup[] extra) =>
        new(
            new ConfigurationBuilder().AddInMemoryCollection(environment).Build(),
            [new CoreSettings(), .. extra],
            new SettingStore(null!),
            TimeProvider.System,
            NullLogger<SettingsService>.Instance);

    [Fact]
    public void The_catalog_joins_every_group()
    {
        var service = Service(
            new Dictionary<string, string?>(),
            new Group(new SettingDefinition("llm.model", SettingType.String, null, SettingValidators.MaxLength(128))));

        Assert.Contains(service.Definitions, d => d.Key == "llm.model");
        Assert.Contains(service.Definitions, d => d.Key == "audio.retentionDays");
    }

    [Fact]
    public void Two_groups_with_one_key_are_a_mistake()
    {
        var service = Service(
            new Dictionary<string, string?>(),
            new Group(new SettingDefinition("audio.retentionDays", SettingType.Int, "1", _ => null)));

        var error = Assert.Throws<InvalidOperationException>(() => service.Definitions);

        Assert.Contains("audio.retentionDays", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_added_by_another_track_resolves_from_its_environment_variable()
    {
        var service = Service(
            new Dictionary<string, string?> { ["Nytka:Llm:BaseUrl"] = "http://llm.test/v1" },
            new Group(new SettingDefinition("llm.baseUrl", SettingType.Url, null, SettingValidators.BaseUrl)));

        Assert.Equal("http://llm.test/v1", service.Get("llm.baseUrl"));
        Assert.True(service.IsLocked(service.Find("llm.baseUrl")!));
    }

    [Fact]
    public void An_environment_value_that_breaks_the_rules_names_its_variable()
    {
        var service = Service(new Dictionary<string, string?> { ["Nytka:Audio:RetentionDays"] = "lots" });

        var error = Assert.Throws<InvalidOperationException>(() => service.Get("audio.retentionDays"));

        Assert.Contains("Nytka__Audio__RetentionDays", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("lots", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_environment_value_is_unset()
    {
        var service = Service(new Dictionary<string, string?> { ["Nytka:Audio:RetentionDays"] = "" });

        Assert.Equal("14", service.Get("audio.retentionDays"));
        Assert.False(service.IsLocked(service.Find("audio.retentionDays")!));
    }

    [Fact]
    public void A_key_that_does_not_exist_is_a_mistake() =>
        Assert.Throws<ArgumentException>(() => Service(new Dictionary<string, string?>()).Get("audio.nothing"));

    [Theory]
    [InlineData("stt.url", true)]
    [InlineData("stt.apiKey", true)]
    [InlineData("stt.model", false)]
    [InlineData("conversations.gap", false)]
    public void A_secret_and_the_stt_url_are_locked_whatever_the_environment_holds(string key, bool locked)
    {
        var service = Service(new Dictionary<string, string?>());

        Assert.Equal(locked, service.IsLocked(service.Find(key)!));
    }
}

public class SettingValidatorsTests
{
    [Theory]
    [InlineData("http://stt.test/v1/audio/transcriptions", true)]
    [InlineData("https://api.openai.com/v1/audio/transcriptions", true)]
    [InlineData("ftp://stt.test/", false)]
    [InlineData("/relative", false)]
    [InlineData("stt.test", false)]
    [InlineData("", false)]
    public void Url_is_an_absolute_http_or_https_url(string value, bool valid) =>
        Assert.Equal(valid, SettingValidators.Url(value) is null);

    [Theory]
    [InlineData("http://llm.test/v1", true)]
    [InlineData("http://llm.test/v1/", true)]
    [InlineData("http://llm.test/v1?key=x", false)]
    [InlineData("http://llm.test/v1#top", false)]
    [InlineData("llm.test/v1", false)]
    public void A_base_url_has_no_query_or_fragment(string value, bool valid) =>
        Assert.Equal(valid, SettingValidators.BaseUrl(value) is null);

    [Fact]
    public void Messages_never_quote_the_value() =>
        Assert.DoesNotContain("secret-host", SettingValidators.Url("ftp://user:pw@secret-host/") ?? "", StringComparison.Ordinal);

    [Theory]
    [InlineData("auto", true)]
    [InlineData("uk", true)]
    [InlineData("fil", true)]
    [InlineData("en-GB", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("AUTO", false)]
    [InlineData("u", false)]
    [InlineData("english", false)]
    [InlineData("en_GB", false)]
    [InlineData("en-", false)]
    public void Language_is_auto_or_a_tag(string value, bool valid) =>
        Assert.Equal(valid, SettingValidators.Language(value) is null);

    [Theory]
    [InlineData("00:00:30", true)]
    [InlineData("00:02:00", true)]
    [InlineData("01:00:00", true)]
    [InlineData("00:00:29", false)]
    [InlineData("01:00:01", false)]
    [InlineData("2:00", false)]
    [InlineData("120", false)]
    [InlineData("-00:02:00", false)]
    public void Duration_is_hh_mm_ss_within_its_bounds(string value, bool valid) =>
        Assert.Equal(
            valid,
            SettingValidators.Duration(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1))(value) is null);

    [Theory]
    [InlineData("0", true)]
    [InlineData("3650", true)]
    [InlineData("3651", false)]
    [InlineData("-1", false)]
    [InlineData("1.5", false)]
    [InlineData("+5", false)]
    [InlineData("", false)]
    public void Int_is_a_whole_number_within_its_bounds(string value, bool valid) =>
        Assert.Equal(valid, SettingValidators.Int(0, 3650)(value) is null);

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("True", false)]
    [InlineData("1", false)]
    public void Bool_is_true_or_false(string value, bool valid) =>
        Assert.Equal(valid, SettingValidators.Bool(value) is null);

    [Fact]
    public void Max_length_counts_characters()
    {
        Assert.Null(SettingValidators.MaxLength(3)("abc"));
        Assert.NotNull(SettingValidators.MaxLength(3)("abcd"));
    }
}
