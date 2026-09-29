using Nytka.Server.Ai;

namespace Nytka.Server.Tests.Ai;

public class TextFingerprintTests
{
    [Theory]
    [InlineData("Call Anna tomorrow", "call anna tomorrow")]
    [InlineData("  Call   Anna,\ttomorrow!\n", "call anna tomorrow")]
    [InlineData("Pay 20 UAH by the 5th.", "pay 20 uah by the 5th")]
    [InlineData("Купити МОЛОКО, яйця!", "купити молоко яйця")]
    [InlineData("Зателефонувати Мамі (ЗАВТРА)", "зателефонувати мамі завтра")]
    [InlineData("Don't forget", "don t forget")]
    [InlineData("Call 📞 Anna", "call anna")]
    [InlineData("𠮷野家!", "𠮷野家")]
    [InlineData("!!! ... ---", "")]
    [InlineData("", "")]
    public void Keeps_lower_case_letters_and_digits_with_single_spaces(string text, string fingerprint) =>
        Assert.Equal(fingerprint, TextFingerprint.Of(text));

    [Fact]
    public void The_same_task_in_other_casing_and_punctuation_has_the_same_fingerprint() =>
        Assert.Equal(TextFingerprint.Of("Call Anna, tomorrow!"), TextFingerprint.Of("call anna - TOMORROW"));

    [Fact]
    public void Other_words_make_another_fingerprint()
    {
        Assert.NotEqual(TextFingerprint.Of("Call Anna"), TextFingerprint.Of("Call Anton"));
        Assert.NotEqual(TextFingerprint.Of("Call Anna"), TextFingerprint.Of("CallAnna"));
    }
}
