using Nytka.Server.Settings;

namespace Nytka.Server.Tests.Settings;

public class SettingDefinitionTests
{
    private static SettingDefinition Define(string key, SettingType type = SettingType.String, string? defaultValue = null) =>
        new(key, type, defaultValue, _ => null);

    [Theory]
    [InlineData("stt.url", "Nytka__Stt__Url")]
    [InlineData("stt.apiKey", "Nytka__Stt__ApiKey")]
    [InlineData("llm.baseUrl", "Nytka__Llm__BaseUrl")]
    [InlineData("llm.outputLanguage", "Nytka__Llm__OutputLanguage")]
    [InlineData("conversations.gap", "Nytka__Conversations__Gap")]
    [InlineData("audio.retentionDays", "Nytka__Audio__RetentionDays")]
    [InlineData("memories.userName", "Nytka__Memories__UserName")]
    public void A_key_names_its_environment_variable(string key, string variable) =>
        Assert.Equal(variable, Define(key).EnvironmentVariable);

    [Fact]
    public void The_environment_beats_the_table_and_the_default() =>
        Assert.Equal(
            new ResolvedSetting("30", SettingSource.Env),
            Define("audio.retentionDays", SettingType.Int, "14").Resolve(environment: "30", table: "7"));

    [Fact]
    public void The_table_beats_the_default() =>
        Assert.Equal(
            new ResolvedSetting("7", SettingSource.Db),
            Define("audio.retentionDays", SettingType.Int, "14").Resolve(environment: null, table: "7"));

    [Fact]
    public void The_default_applies_when_nothing_else_is_set()
    {
        Assert.Equal(
            new ResolvedSetting("14", SettingSource.Default),
            Define("audio.retentionDays", SettingType.Int, "14").Resolve(null, null));
        Assert.Equal(new ResolvedSetting(null, SettingSource.Default), Define("llm.model").Resolve(null, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_empty_environment_variable_counts_as_unset_and_never_shadows_the_table(string? environment)
    {
        var retention = Define("audio.retentionDays", SettingType.Int, "14");

        Assert.Equal(new ResolvedSetting("7", SettingSource.Db), retention.Resolve(environment, "7"));
        Assert.Equal(new ResolvedSetting("14", SettingSource.Default), retention.Resolve(environment, null));
    }

    [Fact]
    public void An_empty_row_counts_as_unset() =>
        Assert.Equal(
            new ResolvedSetting("14", SettingSource.Default),
            Define("audio.retentionDays", SettingType.Int, "14").Resolve(null, ""));

    [Theory]
    [InlineData(SettingType.String, false)]
    [InlineData(SettingType.Url, false)]
    [InlineData(SettingType.Secret, true)]
    [InlineData(SettingType.Duration, false)]
    [InlineData(SettingType.Int, false)]
    [InlineData(SettingType.Language, false)]
    [InlineData(SettingType.Bool, false)]
    public void Only_a_secret_is_a_secret(SettingType type, bool secret) =>
        Assert.Equal(secret, Define("some.key", type).IsSecret);

    [Fact]
    public void A_secret_comes_only_from_the_environment()
    {
        var apiKey = Define("llm.apiKey", SettingType.Secret);

        Assert.Equal(new ResolvedSetting("sk-from-env", SettingSource.Env), apiKey.Resolve("sk-from-env", "sk-from-table"));
        Assert.Equal(new ResolvedSetting(null, SettingSource.Default), apiKey.Resolve(null, "sk-from-table"));
        Assert.Equal(new ResolvedSetting(null, SettingSource.Default), apiKey.Resolve("", "sk-from-table"));
    }
}
