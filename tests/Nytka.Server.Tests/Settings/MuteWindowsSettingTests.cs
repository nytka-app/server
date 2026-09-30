using Nytka.Server.Settings;

namespace Nytka.Server.Tests.Settings;

public sealed class MuteWindowsSettingTests
{
    private static SettingDefinition Definition => new CoreSettings().Definitions.Single(d => d.Key == "mute.windows");

    [Fact]
    public void Defaults_to_an_empty_list_and_reads_its_variable()
    {
        Assert.Equal((SettingType.String, "[]", "Nytka__Mute__Windows"), (Definition.Type, Definition.Default, Definition.EnvironmentVariable));
        Assert.Null(Definition.Validate("[]"));
    }

    [Fact]
    public void Accepts_the_contract_example_and_refuses_bad_values_with_a_message()
    {
        Assert.Null(Definition.Validate("""[{"days":[1,2,3,4,5],"start":"09:30","end":"10:00"}]"""));
        Assert.NotNull(Definition.Validate("""[{"days":[1],"start":"9:30","end":"10:00"}]"""));
        Assert.NotNull(Definition.Validate("[" + new string(' ', 9000) + "]"));
    }
}
