using Nytka.Audio.Voice;

namespace Nytka.Audio.Tests.Voice;

/// <summary>
/// A test that needs TitaNet-small beside the test binaries (scripts/fetch-speaker-model.sh, then build). It is
/// skipped with a message when the file is missing, unless <c>NYTKA_REQUIRE_SPEAKER_MODEL</c> is set, as CI
/// sets it, so a broken fetch fails the build instead of skipping the tests.
/// </summary>
public sealed class SpeakerModelFactAttribute : FactAttribute
{
    public SpeakerModelFactAttribute()
    {
        if (!File.Exists(SpeakerEmbedder.DefaultPath) && Environment.GetEnvironmentVariable("NYTKA_REQUIRE_SPEAKER_MODEL") is null)
        {
            Skip = "No speaker model beside the test binaries; run scripts/fetch-speaker-model.sh and build again.";
        }
    }
}
