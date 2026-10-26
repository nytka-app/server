using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Nytka.Audio.Frames;
using Nytka.Server.Auth;
using Nytka.Server.People;
using Nytka.Server.Search;
using Nytka.Server.Settings;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Api;

public static class InfoEndpoints
{
    public const int ApiVersion = 1;

    /// <summary>The release version without the "+commit" suffix the SDK appends.</summary>
    public static string ServerVersion { get; } =
        typeof(InfoEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";

    /// <summary>
    /// What this server can do beyond apiVersion 1, for the app to gate on. "offline-sync": late audio
    /// queues behind live speech and merges by capture time (migration 0005). "voice": the speaker model is
    /// there, so the voice routes work (docs/specs/your-voice.md). "people": notes on people and a person on a segment
    /// (docs/specs/people.md). "review": <c>GET /api/v1/review</c> exists. "briefs": <c>GET /api/v1/briefs/upcoming</c> exists, with or
    /// without a calendar feed (the app reads <c>calendar.icsUrl</c> from the settings). "voice-groups": voice matching of other people is on, the model is there and speech audio is
    /// kept for a day or more, since fingerprints die with it. "tags": tags on conversations and people (docs/specs/tags.md). "tag-suggestions": proposed tags for conversations,
    /// <c>GET /api/v1/tags/suggestions</c> and the review kind <c>tag</c>. "roles": a name suggestion may carry a role, and people have <c>named</c> (docs/specs/tags.md, Roles).
    /// "speech-kind": a line has a speech kind and the owner marks it (docs/specs/speech-kind.md): <c>speechKind</c> on <c>PATCH /api/v1/segments/{id}</c>,
    /// <c>POST /api/v1/conversations/{id}/speech</c>, <c>mediaShare</c> and <c>media=</c> on the conversation list.
    /// "context-ranges": the app may send <c>POST /api/v1/context/ranges</c> (docs/specs/speech-kind.md, Context from the phone).
    /// "context-device", "context-activity", "context-now-playing", "context-gain": the app may send the kinds <c>screen</c> and
    /// <c>headphones</c>, <c>activity</c>, <c>now-playing</c> and <c>gain</c>; a row of a kind the server does not know is rejected on its own.
    /// "task-kinds": a task has a <c>kind</c> (<c>commitment</c> or <c>idea</c>), <c>GET /api/v1/tasks</c> takes <c>kind=</c>, and <c>GET /api/v1/notes</c> exists (docs/specs/task-kinds.md).
    /// "waiting-on": a task may have the kind <c>waiting_on</c> (what another person promised the wearer), <c>kind=waiting_on</c> lists them and the person page has <c>waitingOn</c> (docs/specs/task-kinds.md, Waiting on).
    /// "ledger": <c>GET /api/v1/people/{id}/ledger</c> and the MCP tool <c>get_ledger</c> exist (docs/specs/promise-ledger.md).
    /// "calendar-feed": <c>GET /api/v1/calendar.ics</c> exists (NYTKA-78), with or without a calendar feed.
    /// "auto-names": a name suggestion may be <c>auto_applied</c>, <c>GET /api/v1/people/suggestions?status=auto_applied</c> lists them and
    /// <c>POST /api/v1/people/suggestions/{id}/undo</c> takes one back (docs/specs/people.md, Auto-applied names), whether or not auto-apply is on.
    /// "review-clips": review items carry <c>clip</c> and <c>context</c>, and <c>GET /api/v1/review/{kind}/{id}/clip</c> exists.
    /// "transcript-upgrade": <c>POST /api/v1/conversations/{id}/upgrade</c> exists, and a conversation carries <c>transcript</c> (docs/specs/auto-transcription.md).
    /// "usage": <c>GET /api/v1/usage</c> exists, with the <c>stt.prices</c> and <c>stt.monthlyBudgetUsd</c> settings (docs/specs/usage.md).
    /// "summary-templates": <c>/api/v1/summary-templates</c> exists, a conversation has <c>summaryTemplate</c>, and the <c>summary.templates.*</c> settings (NYTKA-82).
    /// "quality": <c>GET /api/v1/quality</c> exists, the nightly quality metrics.
    /// "labeling": the labelling routes under <c>/api/v1/labeling</c> exist, and tokens may have the scope <c>labeling</c>.
    /// "topics": <c>GET /api/v1/topics</c> exists and a conversation has <c>topics</c>; pages fill while <c>topics.enabled</c> is on.
    /// "rollups": <c>GET /api/v1/rollups</c> exists; rollups are made while <c>rollups.enabled</c> is on.
    /// "related": <c>GET /api/v1/related</c> exists and a conversation has <c>related</c>, its "Earlier" links (empty until the vectors are ready).
    /// "today": <c>GET /api/v1/today</c> and <c>GET /api/v1/timeline</c> exist, items carry <c>evidence</c>, and tasks, memories and notes take your <c>salience</c> mark.
    /// "events": <c>GET /api/v1/events</c> and the MCP tool <c>list_events</c> exist, a day's derived structure (docs/explanation/features.md#day-events).
    /// "semantic-search": an embeddings endpoint is set and its vectors are ready, so search and Ask also match by meaning (NYTKA-66).
    /// "insights": <c>GET /api/v1/insights</c> exists, the "Over time" baselines and unlock progress (NYTKA-147).
    /// "nudges": <c>GET /api/v1/nudges</c> exists, what the app shows as notifications (NYTKA-148).
    /// </summary>
    public static IReadOnlyList<string> Features(SpeakerModel voice, SettingsService settings, EmbeddingState? embeddings = null)
    {
        var features = voice.Available ? new List<string> { "offline-sync", "voice", "people", "review", "briefs" } : ["offline-sync", "people", "review", "briefs"];
        if (voice.Available && PeopleSettings.VoiceMatching(settings) && int.Parse(settings.Get("audio.retentionDays")!, CultureInfo.InvariantCulture) >= 1)
        {
            features.Add("voice-groups");
        }

        features.Add("tags");
        features.Add("tag-suggestions");
        features.Add("roles");
        features.Add("speech-kind");
        features.Add("context-ranges");
        features.AddRange(ContextRangeStore.Features);
        features.Add("task-kinds");
        features.Add("waiting-on");
        features.Add("task-create");
        features.Add("ledger");
        features.Add("calendar-feed");
        features.Add("auto-names");
        features.Add("review-clips");
        features.Add("usage");
        features.Add("transcript-upgrade");
        features.Add("summary-templates");
        features.Add("quality");
        features.Add("labeling");
        features.Add("topics");
        features.Add("rollups");
        features.Add("related");
        features.Add("today");
        features.Add("events");
        features.Add("insights");
        features.Add("nudges");
        if (embeddings?.IsReady == true)
        {
            features.Add("semantic-search");
        }

        return features;
    }

    public static RouteGroupBuilder MapInfo(this RouteGroupBuilder api)
    {
        // The app reads scope to refuse a read token; a server without it counts as admin.
        api.MapGet("/info", (ClaimsPrincipal user, SpeakerModel voice, SettingsService settings, EmbeddingState embeddings) => Results.Ok(new InfoResponse(
            ServerVersion, ApiVersion, user.FindFirstValue(NytkaAuthenticationHandler.ScopeClaim) ?? NytkaScopes.Read, Features(voice, settings, embeddings), ChunkFormat.AcceptedCodecs)))
            .AllowRead();
        return api;
    }

    public sealed record InfoResponse(string ServerVersion, int ApiVersion, string Scope, IReadOnlyList<string> Features, IReadOnlyList<int> Codecs);
}
