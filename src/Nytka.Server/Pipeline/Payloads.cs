namespace Nytka.Server.Pipeline;

public sealed record SessionPayload(Guid SessionId);

public sealed record BatchPayload(long BatchId);

/// <summary><paramref name="Reason"/> is the <c>transcript_passes.reason</c> a new pass gets; a pass already in flight keeps its own.</summary>
public sealed record TranscribeConversationPayload(Guid ConversationId, string Reason);

public sealed record EnrichPayload(Guid ConversationId, bool Force);

public sealed record ExtractPayload(Guid ConversationId);

public sealed record SuggestNamesPayload(Guid ConversationId);

/// <summary>One occurrence of a calendar event: its <c>UID</c> and start.</summary>
public sealed record BriefPayload(string Uid, DateTimeOffset StartsAt);

public sealed record DeliveryPayload(Guid DeliveryId);

/// <summary><paramref name="LocalDate"/> is <c>yyyy-MM-dd</c>; <paramref name="Replace"/> is an on-demand run, which replaces the date's digest.</summary>
public sealed record DigestPayload(string LocalDate, bool Replace);

/// <summary><paramref name="LocalDate"/> is <c>yyyy-MM-dd</c>, the local day <c>measure-quality</c> measures.</summary>
public sealed record QualityPayload(string LocalDate);
