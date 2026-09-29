using Microsoft.Extensions.Primitives;

namespace Nytka.Server.Settings;

/// <summary>
/// Wraps a configuration source so its empty values under <c>Nytka:</c> count as unset. A Compose file
/// passes every optional variable through empty (<c>Nytka__Audio__RetentionDays=</c>), and the options
/// binder fails on an empty number or duration, and would let an empty string shadow the settings table.
/// </summary>
public sealed class NonEmptyConfigurationSource(IConfigurationSource inner) : IConfigurationSource
{
    public IConfigurationSource Inner { get; } = inner;

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new NonEmptyConfigurationProvider(Inner.Build(builder));
}

public sealed class NonEmptyConfigurationProvider(IConfigurationProvider inner) : IConfigurationProvider, IDisposable
{
    private const string Prefix = NytkaOptions.Section + ":";

    public bool TryGet(string key, out string? value)
    {
        if (!inner.TryGet(key, out value))
        {
            return false;
        }

        if (value?.Length == 0 && key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return false;
        }

        return true;
    }

    public void Set(string key, string? value) => inner.Set(key, value);

    public IChangeToken GetReloadToken() => inner.GetReloadToken();

    public void Load() => inner.Load();

    public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) =>
        inner.GetChildKeys(earlierKeys, parentPath);

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
