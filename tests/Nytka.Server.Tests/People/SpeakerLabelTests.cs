using Nytka.Storage;

namespace Nytka.Server.Tests.People;

public class SpeakerLabelTests
{
    [Theory]
    [InlineData("SPEAKER_0", "Voice 1")]
    [InlineData("SPEAKER_146", "Voice 147")]
    [InlineData("Anna", "Anna")]
    [InlineData("SPEAKER_", "SPEAKER_")]
    [InlineData("SPEAKER_x", "SPEAKER_x")]
    [InlineData(null, null)]
    public void A_raw_provider_label_reads_as_a_numbered_voice(string? stored, string? shown) =>
        Assert.Equal(shown, SpeakerLabel.Display(stored));
}
