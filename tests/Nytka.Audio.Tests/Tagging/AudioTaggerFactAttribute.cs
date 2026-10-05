using Nytka.Audio.Tagging;

namespace Nytka.Audio.Tests.Tagging;

/// <summary>
/// A test that needs YAMNet and its class map beside the test binaries (scripts/fetch-audio-tagger.sh, then build). It is skipped
/// with a message when they are missing, unless <c>NYTKA_REQUIRE_AUDIO_TAGGER</c> is set, as CI sets it, so a broken fetch fails
/// the build instead of skipping the test.
/// </summary>
public sealed class AudioTaggerFactAttribute : FactAttribute
{
    public AudioTaggerFactAttribute()
    {
        if (!AudioTagger.FilesPresent && Environment.GetEnvironmentVariable("NYTKA_REQUIRE_AUDIO_TAGGER") is null)
        {
            Skip = "No audio tagger beside the test binaries; run scripts/fetch-audio-tagger.sh and build again.";
        }
    }
}
