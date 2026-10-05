using Nytka.Audio.Tagging;

namespace Nytka.Server.Speech;

/// <summary>
/// YAMNet, loaded on first use. Without its files, or when they fail to load, <see cref="Tagger"/> is null and a guess runs with
/// the audio features missing (signal <c>partial</c>).
/// </summary>
public sealed class AudioTaggerModel : IDisposable
{
    private readonly Lazy<IAudioTagger?> _loaded;

    /// <summary>A model that is already there, such as a test's fake; null means none.</summary>
    public AudioTaggerModel(IAudioTagger? tagger) => _loaded = new(tagger);

    private AudioTaggerModel(Func<IAudioTagger?> load) => _loaded = new(load);

    public IAudioTagger? Tagger => _loaded.Value;

    public static AudioTaggerModel FromFiles(string modelPath, string classMapPath, ILogger<AudioTaggerModel> logger) => new(() =>
    {
        if (!File.Exists(modelPath) || !File.Exists(classMapPath))
        {
            logger.LogInformation("Speech: no audio tagger beside the binaries; guesses run from structure alone.");
            return null;
        }

        try
        {
            return new AudioTagger(modelPath, classMapPath);
        }
        catch (Exception error)
        {
            logger.LogError("Speech: the audio tagger failed to load ({ExceptionType}); guesses run from structure alone.", error.GetType().Name);
            return null;
        }
    });

    public void Dispose()
    {
        if (_loaded.IsValueCreated && _loaded.Value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
