using System.Security.Cryptography;
using Nytka.Audio.Voice;

namespace Nytka.Server.Voice;

/// <summary>
/// The speaker model, loaded on first use: nothing touches the file until a voiceprint exists. Without the
/// file, or when it fails to load, <see cref="Embedder"/> is null and segments keep the provider's labels.
/// <see cref="Id"/> is the file's SHA-256, stored with every vector so vectors of another model are never compared.
/// </summary>
public sealed class SpeakerModel : IDisposable
{
    private readonly Lazy<(ISpeakerEmbedder Embedder, string Id)?> _loaded;

    public SpeakerModel(ISpeakerEmbedder embedder, string id) => _loaded = new(() => (embedder, id));

    private SpeakerModel(Func<(ISpeakerEmbedder, string)?> load) => _loaded = new(load);

    public ISpeakerEmbedder? Embedder => _loaded.Value?.Embedder;

    public string? Id => _loaded.Value?.Id;

    public static SpeakerModel FromFile(string path, ILogger<SpeakerModel> logger) => new(() =>
    {
        if (!File.Exists(path))
        {
            logger.LogInformation("Voice: no speaker model at {Path}; segments keep the provider's labels.", path);
            return null;
        }

        try
        {
            using var file = File.OpenRead(path);
            var id = Convert.ToHexStringLower(SHA256.HashData(file));
            return (new SpeakerEmbedder(path), id);
        }
        catch (Exception error)
        {
            logger.LogError("Voice: the speaker model failed to load ({ExceptionType}); segments keep the provider's labels.", error.GetType().Name);
            return null;
        }
    });

    public void Dispose()
    {
        if (_loaded.IsValueCreated && _loaded.Value?.Embedder is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
