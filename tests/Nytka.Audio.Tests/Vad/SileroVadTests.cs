using Nytka.Audio.Vad;

namespace Nytka.Audio.Tests.Vad;

public class SileroVadTests
{
    [Fact]
    public void Silence_is_not_speech()
    {
        using var vad = new SileroVad();
        var window = new float[vad.WindowSamples];

        for (var i = 0; i < 30; i++)
        {
            Assert.InRange(vad.Probability(window), 0f, 0.5f);
        }
    }

    [Fact]
    public void Rejects_a_wrong_window_size()
    {
        using var vad = new SileroVad();

        Assert.Throws<ArgumentException>(() => vad.Probability(new float[100]));
    }
}
