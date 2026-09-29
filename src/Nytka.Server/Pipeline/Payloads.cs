namespace Nytka.Server.Pipeline;

public sealed record SessionPayload(Guid SessionId);

public sealed record BatchPayload(long BatchId);
