namespace Nytka.Storage;

/// <summary>A job handed to the runner. <see cref="Payload"/> is the jsonb payload as text.</summary>
public sealed record JobRecord(long Id, string Kind, string Payload, int Attempts);
