using Dapper;
using Npgsql;
using OmiPlatform.Omi;

namespace OmiPlatform.Storage;

/// <summary>
/// Writes to <c>omi_raw</c>, the declared source of truth. Nothing here interprets a payload —
/// that is <see cref="DocumentProjector"/>'s job, and a payload no model can parse must still land
/// here.
/// </summary>
/// <remarks>
/// Plain per-row upserts rather than Oura's <c>NpgsqlBinaryImporter</c> bulk path: a personal
/// conversation/memory feed is tens to low hundreds of documents per cycle, not the thousands of
/// 5-minute samples a night of Oura data expands into. The simpler path is correct here; reach for
/// <c>COPY</c> only if a batch size stops being "a few hundred".
/// </remarks>
public sealed class RawDocumentRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public RawDocumentRepository(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<int> UpsertAsync(
        IReadOnlyList<OmiRawDocument> documents,
        string specVersion,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into omi_raw (doc_type, doc_id, day, payload, spec_ver, fetched_at)
            values (@DocType, @DocId, @Day, @Payload::jsonb, @SpecVersion, now())
            on conflict (doc_type, doc_id) do update set
                day        = excluded.day,
                payload    = excluded.payload,
                spec_ver   = excluded.spec_ver,
                fetched_at = excluded.fetched_at
            """,
            documents.Select(document => new
            {
                document.DocType,
                document.DocId,
                document.Day,
                document.Payload,
                SpecVersion = specVersion,
            }),
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return affected;
    }

    /// <summary>Replays stored payloads for a doc type so projections can be rebuilt without
    /// re-calling the API. This is the invariant <c>omi_raw</c> exists to serve.</summary>
    public async Task<IReadOnlyList<OmiRawDocument>> ReadAsync(
        string docType,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<RawRow>(new CommandDefinition(
            """
            select doc_type as DocType, doc_id as DocId, day as Day, payload as Payload
            from omi_raw
            where doc_type = @docType
            order by day nulls first, doc_id
            """,
            new { docType },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(row => new OmiRawDocument(row.DocType, row.DocId, row.Day, row.Payload)).ToArray();
    }

    private sealed record RawRow(string DocType, string DocId, DateOnly? Day, string Payload);
}
