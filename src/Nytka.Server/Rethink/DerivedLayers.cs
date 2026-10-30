using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.People;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Server.Situations;
using Nytka.Server.Tracks;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Rethink;

// The layers on main. Each Rules constant is a revision to bump when a change to the layer's code makes it build different rows from
// the same input (the clusterer, the cut, the naming rules): a bump is a new version of the layer, so its days become stale.

/// <summary>Speaker tracks (<c>build-tracks</c>): the speaker model, the clustering threshold and the voiceprint threshold of the suggestions.</summary>
public sealed class TracksLayer(
    DerivationStore store, JobQueue queue, SpeakerModel model, SettingsService settings, TimeProvider time) : IDerivedLayer
{
    public const string Rules = "1";

    public string Name => DerivedLayers.Tracks;

    /// <summary>The links by meaning are soft constraints of a build while <c>tracks.meaning</c> is on.</summary>
    public IReadOnlyList<string> DependsOn => TrackSettings.Meaning(settings) ? [DerivedLayers.Links] : [];

    public bool Enabled => TrackSettings.Enabled(settings) && model.Id is not null;

    public bool CallsModel => false;

    public LayerInputs Inputs() => new(
        model.Id ?? "", Rules,
        new Dictionary<string, string?>
        {
            [TrackSettings.ThresholdKey] = settings.Get(TrackSettings.ThresholdKey),
            [TrackSettings.MeaningKey] = TrackSettings.Meaning(settings).ToString(),
            [PeopleSettings.VoiceThresholdKey] = settings.Get(PeopleSettings.VoiceThresholdKey),
        });

    public async Task<IReadOnlyList<DateOnly>> StaleDaysAsync(LayerVersion version, CancellationToken ct) =>
        model.Id is { } id ? await store.StaleTrackDaysAsync(version, id, ct) : [];

    public Task<LayerChars> EstimateAsync(DateOnly day, LayerVersion version, CancellationToken ct) => Task.FromResult(default(LayerChars));

    public Task RebuildAsync(DateOnly day, LayerVersion version, CancellationToken ct)
    {
        var text = DigestDay.Text(day);
        return queue.EnqueueAsync(JobKinds.BuildTracks, new TracksPayload(text), JobKinds.BuildTracksKey(text), time.GetUtcNow(), ct);
    }
}

/// <summary>Links by meaning (<c>link-speakers</c>): the text model and the prompt.</summary>
public sealed class LinksLayer(
    DerivationStore store, LinkQueue linkQueue, IOptionsMonitor<LlmOptions> llm, ILlmClient client, SettingsService settings) : IDerivedLayer
{
    public const string Rules = "1";

    public string Name => DerivedLayers.Links;

    public IReadOnlyList<string> DependsOn => [];

    public bool Enabled => TrackSettings.Meaning(settings) && client.IsConfigured;

    public bool CallsModel => true;

    public LayerInputs Inputs() => new(llm.CurrentValue.Model ?? "", $"{Rules}\n{LinkPrompt.System}\n{LinkPrompt.Schema}", new Dictionary<string, string?>());

    public Task<IReadOnlyList<DateOnly>> StaleDaysAsync(LayerVersion version, CancellationToken ct) => store.StaleLinkDaysAsync(version, ct);

    public Task<LayerChars> EstimateAsync(DateOnly day, LayerVersion version, CancellationToken ct) => store.LinkCharsAsync(day, ct);

    /// <summary>
    /// Forgets the day's runs and queues every scene again, as a build of the day does for scenes not asked. The last answer queues the
    /// build of the day's tracks, which follows the new links.
    /// </summary>
    public async Task RebuildAsync(DateOnly day, LayerVersion version, CancellationToken ct)
    {
        await store.ForgetLinkRunsAsync(day, ct);
        if (SituationSettings.Enabled(settings))
        {
            await linkQueue.QueueSituationsAsync(day, ct);
        }
        else
        {
            var (from, to) = DigestDay.Bounds(day, UserTimeZone.Resolve(settings));
            await linkQueue.QueueAsync(day, from, to, ct);
        }
    }
}

/// <summary>Names from phone calls and the calendar (<c>name-tracks</c>): rules and the two settings, over the day's tracks.</summary>
public sealed class NamesLayer(DerivationStore store, JobQueue queue, SettingsService settings, TimeProvider time) : IDerivedLayer
{
    public const string Rules = "1";

    public string Name => DerivedLayers.Names;

    public IReadOnlyList<string> DependsOn => [DerivedLayers.Tracks];

    public bool Enabled => TrackSettings.Enabled(settings) && (PeopleSettings.CallNames(settings) || PeopleSettings.CalendarNames(settings));

    public bool CallsModel => false;

    public LayerInputs Inputs() => new(
        "", Rules,
        new Dictionary<string, string?>
        {
            [PeopleSettings.CallNamesKey] = PeopleSettings.CallNames(settings).ToString(),
            [PeopleSettings.CalendarNamesKey] = PeopleSettings.CalendarNames(settings).ToString(),
        });

    public Task<IReadOnlyList<DateOnly>> StaleDaysAsync(LayerVersion version, CancellationToken ct) => store.StaleNameDaysAsync(version, ct);

    public Task<LayerChars> EstimateAsync(DateOnly day, LayerVersion version, CancellationToken ct) => Task.FromResult(default(LayerChars));

    public Task RebuildAsync(DateOnly day, LayerVersion version, CancellationToken ct)
    {
        var text = DigestDay.Text(day);
        return queue.EnqueueAsync(JobKinds.NameTracks, new TracksPayload(text), JobKinds.NameTracksKey(text), time.GetUtcNow(), ct);
    }
}

/// <summary>Stored situations (<c>build-situations</c>): the cut follows the day's tracks and, with <c>tracks.meaning</c> on, its links.</summary>
public sealed class SituationsLayer(DerivationStore store, JobQueue queue, SettingsService settings, TimeProvider time) : IDerivedLayer
{
    public const string Rules = "1";

    public string Name => DerivedLayers.Situations;

    public IReadOnlyList<string> DependsOn => TrackSettings.Meaning(settings) ? [DerivedLayers.Tracks, DerivedLayers.Links] : [DerivedLayers.Tracks];

    public bool Enabled => SituationSettings.Enabled(settings);

    public bool CallsModel => false;

    public LayerInputs Inputs() => new(
        "", Rules,
        new Dictionary<string, string?>
        {
            [TrackSettings.EnabledKey] = TrackSettings.Enabled(settings).ToString(),
            [TrackSettings.MeaningKey] = TrackSettings.Meaning(settings).ToString(),
        });

    public Task<IReadOnlyList<DateOnly>> StaleDaysAsync(LayerVersion version, CancellationToken ct) => store.StaleSituationDaysAsync(version, ct);

    public Task<LayerChars> EstimateAsync(DateOnly day, LayerVersion version, CancellationToken ct) => Task.FromResult(default(LayerChars));

    public Task RebuildAsync(DateOnly day, LayerVersion version, CancellationToken ct)
    {
        var text = DigestDay.Text(day);
        return queue.EnqueueAsync(JobKinds.BuildSituations, new SituationsPayload(text), JobKinds.BuildSituationsKey(text), time.GetUtcNow(), ct);
    }
}

/// <summary>
/// The title and summary of a situation (<c>enrich-situation</c>): the text model and the prompt. It follows no other layer: a new cut
/// gives a situation new lines, and a situation whose lines changed is summarized again anyway; a track change that leaves the lines
/// alone leaves the summary right.
/// </summary>
public sealed class EnrichmentLayer(DerivationStore store, JobQueue queue, IOptionsMonitor<LlmOptions> llm, ILlmClient client, SettingsService settings, TimeProvider time) : IDerivedLayer
{
    public const string Rules = "1";

    private const string Placeholder = "<>";

    public string Name => DerivedLayers.Enrichment;

    public IReadOnlyList<string> DependsOn => [];

    public bool Enabled => SituationSettings.Enabled(settings) && client.IsConfigured;

    public bool CallsModel => true;

    public LayerInputs Inputs() => new(
        llm.CurrentValue.Model ?? "",
        $"{Rules}\n{SituationPrompt.System(Placeholder, Placeholder)}\n{SituationPrompt.SystemForMerge(Placeholder, Placeholder)}\n{SituationPrompt.Schema}",
        new Dictionary<string, string?> { ["llm.outputLanguage"] = llm.CurrentValue.OutputLanguage });

    public Task<IReadOnlyList<DateOnly>> StaleDaysAsync(LayerVersion version, CancellationToken ct) => store.StaleEnrichmentDaysAsync(version, ct);

    public Task<LayerChars> EstimateAsync(DateOnly day, LayerVersion version, CancellationToken ct) => store.EnrichmentCharsAsync(
        day, version, new LayerChars(SituationPrompt.System(Placeholder, Placeholder).Length, SituationPrompt.MaxTitle + SituationPrompt.MaxSummary), ct);

    /// <summary>The old title and summary stay in place until the new ones replace them.</summary>
    public async Task RebuildAsync(DateOnly day, LayerVersion version, CancellationToken ct)
    {
        foreach (var id in await store.MarkSummariesStaleAsync(day, version, ct))
        {
            await queue.EnqueueAsync(JobKinds.EnrichSituation, new EnrichSituationPayload(id), JobKinds.EnrichSituationKey(id), time.GetUtcNow(), ct);
        }
    }
}

/// <summary>The names of the layers on main; the layers after them (facts, nytka-app/server#305) add theirs.</summary>
public static class DerivedLayers
{
    public const string Tracks = "tracks";
    public const string Links = "links";
    public const string Names = "names";
    public const string Situations = "situations";
    public const string Enrichment = "enrichment";
}
