using Nytka.Server.Ai;
using Nytka.Server.Settings;

namespace Nytka.Server.Tests.Ai;

public class UserTimeZoneTests
{
    private static SettingDefinition Definition => new CoreSettings().Definitions.Single(d => d.Key == "user.timeZone");

    [Fact]
    public void The_setting_is_a_string_that_defaults_to_utc() =>
        Assert.Equal((SettingType.String, "UTC", "Nytka__User__TimeZone"), (Definition.Type, Definition.Default, Definition.EnvironmentVariable));

    [Theory]
    [InlineData("Europe/Kyiv", true)]
    [InlineData("UTC", true)]
    [InlineData("America/New_York", true)]
    [InlineData("Mars/Olympus", false)]
    [InlineData("not a zone", false)]
    public void Only_a_known_zone_is_valid(string value, bool valid) =>
        Assert.Equal(valid, Definition.Validate(value) is null);
}
