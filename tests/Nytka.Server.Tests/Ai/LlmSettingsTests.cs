using Nytka.Server.Ai;
using Nytka.Server.Settings;

namespace Nytka.Server.Tests.Ai;

public class LlmSettingsTests
{
    private static SettingDefinition Definition(string key) => new LlmSettings().Definitions.Single(d => d.Key == key);

    [Fact]
    public void Declares_the_llm_keys_with_their_types_and_environment_variables()
    {
        var definitions = new LlmSettings().Definitions;

        Assert.Equal(
            [
                ("llm.baseUrl", SettingType.Url, null, "Nytka__Llm__BaseUrl"),
                ("llm.apiKey", SettingType.Secret, null, "Nytka__Llm__ApiKey"),
                ("llm.model", SettingType.String, null, "Nytka__Llm__Model"),
                ("llm.outputLanguage", SettingType.Language, "auto", "Nytka__Llm__OutputLanguage"),
            ],
            definitions.Select(d => (d.Key, d.Type, d.Default, d.EnvironmentVariable)));
    }

    [Fact]
    public void The_api_key_is_a_secret_that_only_the_environment_supplies()
    {
        var key = Definition("llm.apiKey");

        Assert.True(key.IsSecret);
        Assert.Equal(new ResolvedSetting(null, SettingSource.Default), key.Resolve(null, "from the table"));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("https://api.example.com/v1", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("not a url", false)]
    [InlineData("/relative", false)]
    [InlineData("https://example.com/v1?key=1", false)]
    [InlineData("https://example.com/v1#top", false)]
    public void Validates_the_base_url(string value, bool valid) =>
        Assert.Equal(valid, Definition("llm.baseUrl").Validate(value) is null);

    [Theory]
    [InlineData("auto", true)]
    [InlineData("uk", true)]
    [InlineData("en-GB", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("English", false)]
    [InlineData("", false)]
    [InlineData("u", false)]
    [InlineData("uk_UA", false)]
    public void Validates_the_output_language(string value, bool valid) =>
        Assert.Equal(valid, Definition("llm.outputLanguage").Validate(value) is null);

    [Fact]
    public void A_model_name_is_at_most_128_characters()
    {
        var model = Definition("llm.model");

        Assert.Null(model.Validate(new string('m', 128)));
        Assert.NotNull(model.Validate(new string('m', 129)));
    }
}
