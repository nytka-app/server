using Nytka.Storage;

namespace Nytka.Server.Tests.Tags;

public sealed class TagNameTests
{
    [Theory]
    [InlineData("Repairman", "repairman")]
    [InlineData("#Робота", "робота")]
    [InlineData("  #work  ", "work")]
    [InlineData("dog walker", "dog-walker")]
    [InlineData("dog   walker", "dog-walker")]
    [InlineData("a_b-c", "a_b-c")]
    [InlineData("2026", "2026")]
    [InlineData("ЇЖАК", "їжак")]
    public void Normalizes(string input, string expected) => Assert.Equal(expected, TagName.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("##work")]
    [InlineData("# work")]
    [InlineData("a/b")]
    [InlineData("-work")]
    [InlineData("_work")]
    [InlineData("wor.k")]
    [InlineData("a\tb")]
    public void Refuses(string? input) => Assert.Null(TagName.Normalize(input));

    [Fact]
    public void Allows_32_characters_and_refuses_33()
    {
        Assert.Equal(new string('x', 32), TagName.Normalize(new string('x', 32)));
        Assert.Null(TagName.Normalize(new string('x', 33)));
        Assert.Equal(new string('ж', 32), TagName.Normalize(new string('Ж', 32)));
        Assert.Null(TagName.Normalize(new string('ж', 33)));
    }

    [Theory]
    [InlineData("dog-walker", "Dog walker")]
    [InlineData("repairman", "Repairman")]
    [InlineData("майстер", "Майстер")]
    [InlineData("a", "A")]
    public void Displays_with_a_capital_and_spaces(string name, string expected) => Assert.Equal(expected, TagName.Display(name));
}
