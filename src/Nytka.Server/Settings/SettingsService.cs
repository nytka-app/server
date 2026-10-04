using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Options;
using Nytka.Storage;

namespace Nytka.Server.Settings;

/// <summary>What a <c>PATCH</c> did: it changed the settings, or named keys with bad values, or keys that are locked.</summary>
public abstract record SettingsUpdate
{
    public sealed record Applied : SettingsUpdate;

    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : SettingsUpdate;

    public sealed record Locked(IReadOnlyList<string> Keys) : SettingsUpdate;
}

/// <summary>
/// Resolves every key of the catalog: its environment variable when that is set and not empty, else its
/// row in the <c>settings</c> table, else its default. The table is read into memory by
/// <see cref="ReloadAsync"/>, which the settings loader calls once the migrator has run and an update
/// calls after it writes; until then the environment and the defaults apply. A reload tells
/// <c>IOptionsMonitor&lt;NytkaOptions&gt;</c> (and so <c>IOptions&lt;NytkaOptions&gt;</c>) to rebuild, so a
/// change reaches the next job or request without a restart. The environment is whatever
/// <c>IConfiguration</c> holds: environment variables, and the files and arguments beside them.
/// </summary>
public sealed class SettingsService(
    IConfiguration configuration,
    IEnumerable<ISettingsGroup> groups,
    SettingStore store,
    TimeProvider time,
    ILogger<SettingsService> logger)
    : IOptionsChangeTokenSource<NytkaOptions>
{
    /// <summary>Keys whose value only the environment supplies, though they are not secret: the server refuses to start without them.</summary>
    private static readonly HashSet<string> EnvironmentOnly = new(["stt.url"], StringComparer.Ordinal);

    public static bool IsEnvironmentOnly(string key) => EnvironmentOnly.Contains(key);

    private readonly Lazy<IReadOnlyList<SettingDefinition>> _definitions = new(() => Catalog(groups));

    private readonly SemaphoreSlim _writes = new(1, 1);

    private volatile IReadOnlyDictionary<string, string> _table = new Dictionary<string, string>();

    private CancellationTokenSource _changed = new();

    public IReadOnlyList<SettingDefinition> Definitions => _definitions.Value;

    string? IOptionsChangeTokenSource<NytkaOptions>.Name => Options.DefaultName;

    IChangeToken IOptionsChangeTokenSource<NytkaOptions>.GetChangeToken() => new CancellationChangeToken(_changed.Token);

    public SettingDefinition? Find(string key) => Definitions.FirstOrDefault(d => d.Key == key);

    public ResolvedSetting Resolve(SettingDefinition definition, IReadOnlyDictionary<string, string>? table = null) =>
        definition.Resolve(
            Environment(definition), EnvironmentOnly.Contains(definition.Key) ? null : (table ?? _table).GetValueOrDefault(definition.Key));

    /// <summary>A secret, and a key only the environment supplies, refuses writes; so does any key the environment has set.</summary>
    public bool IsLocked(SettingDefinition definition) =>
        definition.IsSecret || EnvironmentOnly.Contains(definition.Key) || Resolve(definition).Source == SettingSource.Env;

    /// <summary>
    /// The value of a key, or null when nothing sets it. An environment value that breaks the key's
    /// rules stops the caller with a message naming the variable, so the server never starts on it.
    /// </summary>
    public string? Get(string key, IReadOnlyDictionary<string, string>? table = null)
    {
        var definition = Find(key) ?? throw new ArgumentException($"No setting {key}.", nameof(key));
        var resolved = Resolve(definition, table);
        if (resolved.Source == SettingSource.Env && definition.Validate(resolved.Value!) is { } error)
        {
            throw new InvalidOperationException($"{definition.EnvironmentVariable}: {error}");
        }

        return resolved.Value;
    }

    /// <summary>Reads the table again and tells the options to rebuild.</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>
    /// Checks every change and writes them all or none: a <c>null</c> or empty value restores the default.
    /// A key with bad values, or an unknown one, is invalid; a locked one is refused.
    /// </summary>
    public async Task<SettingsUpdate> UpdateAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var locked = new List<string>();
        var changes = new Dictionary<string, string?>();
        foreach (var (key, raw) in values)
        {
            if (Find(key) is not { } definition)
            {
                errors[key] = ["Unknown setting."];
                continue;
            }

            if (IsLocked(definition))
            {
                locked.Add(key);
                continue;
            }

            var value = raw?.Trim();
            if (!string.IsNullOrEmpty(value) && definition.Validate(value) is { } error)
            {
                errors[key] = [error];
                continue;
            }

            changes[key] = string.IsNullOrEmpty(value) ? null : value;
        }

        if (errors.Count > 0)
        {
            return new SettingsUpdate.Invalid(errors);
        }

        if (locked.Count > 0)
        {
            return new SettingsUpdate.Locked(locked);
        }

        await _writes.WaitAsync(ct);
        try
        {
            // The options this table would build must build, before anything is written.
            var candidate = new Dictionary<string, string>(_table, StringComparer.Ordinal);
            foreach (var (key, value) in changes)
            {
                if (value is null)
                {
                    candidate.Remove(key);
                }
                else
                {
                    candidate[key] = value;
                }
            }

            if (!TryBuild(candidate))
            {
                return new SettingsUpdate.Invalid(changes.Keys.ToDictionary(k => k, _ => new[] { "The server cannot use this combination of values." }));
            }

            await store.ApplyAsync(changes, time.GetUtcNow(), ct);
            await LoadAsync(ct);
        }
        finally
        {
            _writes.Release();
        }

        return new SettingsUpdate.Applied();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var rows = await store.LoadAsync(ct);
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in rows)
        {
            // A row the catalog does not know, or one that breaks its rules (edited by hand), is ignored;
            // the log names the key only. A secret never comes from the table.
            if (Find(key) is not { IsSecret: false } definition || string.IsNullOrEmpty(value) || definition.Validate(value) is not null)
            {
                logger.LogWarning("Ignoring the settings row for {Key}: not a valid value of a known key.", key);
                continue;
            }

            table[key] = value;
        }

        // A table the options cannot be built from is never swapped in: the last good one stays.
        if (!TryBuild(table))
        {
            logger.LogError("Keeping the previous settings: the table's values do not build the options.");
            return;
        }

        _table = table;
        var previous = Interlocked.Exchange(ref _changed, new CancellationTokenSource());
        await previous.CancelAsync();
    }

    private bool TryBuild(IReadOnlyDictionary<string, string> table)
    {
        try
        {
            SettingsOptionsSetup.Apply(this, new NytkaOptions(), table);
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private string? Environment(SettingDefinition definition) => configuration[definition.EnvironmentVariable.Replace("__", ":", StringComparison.Ordinal)];

    private static List<SettingDefinition> Catalog(IEnumerable<ISettingsGroup> groups)
    {
        var definitions = groups.SelectMany(g => g.Definitions).ToList();
        var duplicate = definitions.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null
            ? definitions
            : throw new InvalidOperationException($"Two settings groups define {duplicate.Key}.");
    }
}
