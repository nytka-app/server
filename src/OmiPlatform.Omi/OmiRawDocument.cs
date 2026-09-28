namespace OmiPlatform.Omi;

/// <summary>
/// One document exactly as Omi returned it, before anything tries to interpret it.
/// </summary>
/// <param name="DocType">"conversation" or "memory".</param>
/// <param name="DocId">Omi's own id — the natural upsert key.</param>
/// <param name="Day">The document's calendar day, when it has one (a conversation's
/// <c>started_at</c>, a memory's <c>created_at</c>), used for windowed backfill and indexing.</param>
/// <param name="Payload">Raw JSON, exactly as received.</param>
public sealed record OmiRawDocument(string DocType, string DocId, DateOnly? Day, string Payload);
