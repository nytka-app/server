using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>A conversation's Ogg stream and its entity tag.</summary>
public sealed record PlaybackStream(byte[] Body, string Tag);

/// <summary>
/// The last stream <see cref="AudioEndpoints"/> built. A player asks for one conversation in a burst of range requests,
/// and each build holds several copies of the conversation's audio, so builds run one at a time and the last one is
/// reused while its <see cref="SpeechAudioVersion"/> holds. Keeps one stream, never more.
/// </summary>
public sealed class PlaybackCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (Guid Id, SpeechAudioVersion Version, PlaybackStream Stream)? _last;

    public async Task<PlaybackStream?> GetAsync(
        Guid id, SpeechAudioVersion version, Func<CancellationToken, Task<PlaybackStream?>> build, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_last is { } last && last.Id == id && last.Version == version)
            {
                return last.Stream;
            }

            _last = null; // the old stream can go before the new one is built
            var built = await build(ct);
            if (built is not null)
            {
                _last = (id, version, built);
            }

            return built;
        }
        finally
        {
            _gate.Release();
        }
    }
}
