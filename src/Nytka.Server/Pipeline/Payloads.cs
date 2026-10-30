namespace Nytka.Server.Pipeline;

public sealed record SessionPayload(Guid SessionId);

public sealed record BatchPayload(long BatchId);

/// <summary><paramref name="Reason"/> is the <c>transcript_passes.reason</c> a new pass gets; a pass already in flight keeps its own.</summary>
public sealed record TranscribeConversationPayload(Guid ConversationId, string Reason);

/// <summary><paramref name="Again"/> summarizes a conversation already summarized up to its last line (a backfill run).</summary>
public sealed record EnrichPayload(Guid ConversationId, bool Force, bool Again = false);

/// <summary><paramref name="Again"/> extracts from a conversation already read up to its last line (a backfill run).</summary>
public sealed record ExtractPayload(Guid ConversationId, bool Again = false);

public sealed record SuggestNamesPayload(Guid ConversationId);

/// <summary>One occurrence of a calendar event: its <c>UID</c> and start.</summary>
public sealed record BriefPayload(string Uid, DateTimeOffset StartsAt);

public sealed record DeliveryPayload(Guid DeliveryId);

/// <summary><paramref name="LocalDate"/> is <c>yyyy-MM-dd</c>; <paramref name="Replace"/> is an on-demand run, which replaces the date's digest.</summary>
public sealed record DigestPayload(string LocalDate, bool Replace);

/// <summary><paramref name="LocalDate"/> is <c>yyyy-MM-dd</c>, the local day <c>measure-quality</c> measures.</summary>
public sealed record QualityPayload(string LocalDate);

/// <summary>
/// <paramref name="LocalDate"/> is <c>yyyy-MM-dd</c>, the local day <c>build-tracks</c> builds; <paramref name="AfterLinks"/> marks the build
/// that follows the day's links within its situations, after which the day is cut once more.
/// </summary>
public sealed record TracksPayload(string LocalDate, bool AfterLinks = false);

/// <summary>
/// <paramref name="LocalDate"/> is <c>yyyy-MM-dd</c>, the local day <c>build-situations</c> cuts; <paramref name="Recut"/> is the one cut
/// after the links within the situations: it asks for no links again.
/// </summary>
public sealed record SituationsPayload(string LocalDate, bool Recut = false);

public sealed record EnrichSituationPayload(Guid SituationId);

/// <summary>
/// The scene <c>link-speakers</c> reads: the conversation's lines from <paramref name="SceneStartedAt"/> to <paramref name="SceneEndedAt"/>
/// (null for the conversation's last scene) that fall on the local day <paramref name="LocalDate"/> (<c>yyyy-MM-dd</c>).
/// </summary>
public sealed record LinkSpeakersPayload(Guid ConversationId, DateTime SceneStartedAt, DateTime? SceneEndedAt, string LocalDate);

/// <summary><paramref name="LocalDate"/> is <c>yyyy-MM-dd</c>, the local day <c>measure-insights</c> measures.</summary>
public sealed record InsightPayload(string LocalDate);
