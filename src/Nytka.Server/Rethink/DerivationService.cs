using System.Security.Cryptography;
using System.Text;
using Nytka.Server.Digests;
using Nytka.Storage;

namespace Nytka.Server.Rethink;

/// <summary>A layer's stale days (none while it is off) and the version they are stale against.</summary>
public sealed record LayerStale(IDerivedLayer Layer, LayerVersion Version, IReadOnlyList<DateOnly> Days);

/// <summary>One layer in <c>GET /api/v1/quality</c>: its current version, and how many days an older version made rows of, oldest and newest.</summary>
public sealed record DerivationStatus(
    string Layer, bool Enabled, IReadOnlyList<string> DependsOn, long Version, int StaleDays, string? OldestStale, string? NewestStale);

/// <summary>
/// The version of each derived layer and which days are stale (docs/explanation/features.md, Nightly re-think). A layer's version is
/// the hash of its model, its prompt, its settings and the current versions of the layers it follows, so changing any of them is a new
/// version of the layer and of every layer after it. A version is recorded the first time it is seen.
/// </summary>
public sealed class DerivationService(IEnumerable<IDerivedLayer> layers, DerivationStore store, TimeProvider time)
{
    private readonly IReadOnlyList<IDerivedLayer> _layers = [.. layers];

    /// <summary>The layers, those a layer follows before it.</summary>
    public IReadOnlyList<IDerivedLayer> Ordered()
    {
        var ordered = new List<IDerivedLayer>();
        var visiting = new HashSet<string>();
        foreach (var layer in _layers)
        {
            Visit(layer, ordered, visiting);
        }

        return ordered;
    }

    /// <summary>The layer's current version, recorded if it is new.</summary>
    public Task<LayerVersion> CurrentAsync(string layer, CancellationToken ct) => CurrentAsync(Find(layer), [], ct);

    /// <summary>Records the current version of every layer, so that a change is seen even while nothing else reads it.</summary>
    public async Task RecordAsync(CancellationToken ct)
    {
        foreach (var layer in Ordered())
        {
            await CurrentAsync(layer, [], ct);
        }
    }

    /// <summary>Every layer that is on, with its stale days, in <see cref="Ordered"/> order.</summary>
    public async Task<IReadOnlyList<LayerStale>> StaleAsync(CancellationToken ct)
    {
        var stale = new List<LayerStale>();
        foreach (var layer in Ordered().Where(l => l.Enabled))
        {
            var version = await CurrentAsync(layer, [], ct);
            stale.Add(new LayerStale(layer, version, await layer.StaleDaysAsync(version, ct)));
        }

        return stale;
    }

    public async Task<IReadOnlyList<DerivationStatus>> StatusAsync(CancellationToken ct)
    {
        var stale = (await StaleAsync(ct)).ToDictionary(s => s.Layer.Name);
        var status = new List<DerivationStatus>();
        foreach (var layer in Ordered())
        {
            var days = stale.TryGetValue(layer.Name, out var s) ? s.Days : [];
            var version = stale.TryGetValue(layer.Name, out s) ? s.Version : await CurrentAsync(layer, [], ct);
            status.Add(new DerivationStatus(
                layer.Name, layer.Enabled, layer.DependsOn, version.Current, days.Count,
                days.Count == 0 ? null : DigestDay.Text(days[0]), days.Count == 0 ? null : DigestDay.Text(days[^1])));
        }

        return status;
    }

    private async Task<LayerVersion> CurrentAsync(IDerivedLayer layer, HashSet<string> path, CancellationToken ct)
    {
        if (!path.Add(layer.Name))
        {
            throw new InvalidOperationException($"The layer {layer.Name} follows itself.");
        }

        var upstream = new List<string>();
        foreach (var dependency in layer.DependsOn.Order(StringComparer.Ordinal))
        {
            upstream.Add($"{dependency}:{(await CurrentAsync(Find(dependency), path, ct)).Current}");
        }

        path.Remove(layer.Name);
        var inputs = layer.Inputs();
        var settings = string.Join('\n', inputs.Settings.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value}"));
        return await store.ResolveAsync(
            layer.Name, Hash(inputs.Model), Hash(inputs.Prompt), Hash(settings), Hash(string.Join(';', upstream)), time.GetUtcNow(), ct);
    }

    private IDerivedLayer Find(string name) =>
        _layers.FirstOrDefault(l => l.Name == name) ?? throw new InvalidOperationException($"No layer is named {name}.");

    private void Visit(IDerivedLayer layer, List<IDerivedLayer> ordered, HashSet<string> visiting)
    {
        if (ordered.Contains(layer))
        {
            return;
        }

        if (!visiting.Add(layer.Name))
        {
            throw new InvalidOperationException($"The layer {layer.Name} follows itself.");
        }

        foreach (var dependency in layer.DependsOn)
        {
            Visit(Find(dependency), ordered, visiting);
        }

        visiting.Remove(layer.Name);
        ordered.Add(layer);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
