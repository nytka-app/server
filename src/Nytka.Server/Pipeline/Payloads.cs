namespace Nytka.Server.Pipeline;

public sealed record SessionPayload(Guid SessionId);

public sealed record BatchPayload(long BatchId);

public sealed record EnrichPayload(Guid ConversationId, bool Force);

public sealed record ExtractPayload(Guid ConversationId);

public sealed record DeliveryPayload(Guid DeliveryId);
