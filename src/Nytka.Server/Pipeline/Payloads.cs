namespace Nytka.Server.Pipeline;

public sealed record SessionPayload(Guid SessionId);

public sealed record BatchPayload(long BatchId);

public sealed record EnrichPayload(Guid ConversationId, bool Force);

public sealed record ExtractPayload(Guid ConversationId);

public sealed record SuggestNamesPayload(Guid ConversationId);

/// <summary>One occurrence of a calendar event: its <c>UID</c> and start.</summary>
public sealed record BriefPayload(string Uid, DateTimeOffset StartsAt);

public sealed record DeliveryPayload(Guid DeliveryId);

/// <summary><paramref name="LocalDate"/> is <c>yyyy-MM-dd</c>; <paramref name="Replace"/> is an on-demand run, which replaces the date's digest.</summary>
public sealed record DigestPayload(string LocalDate, bool Replace);
