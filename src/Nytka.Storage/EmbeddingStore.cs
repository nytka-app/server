using System.Globalization;
using System.Text;
using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A row the indexer has to embed: its id (a <c>long</c> or a <c>Guid</c>) and the text, null when it gets no vector.</summary>
public sealed record EmbeddingInput(object Id, string? Text);

/// <summary>
/// The vectors of segments, conversation titles with summaries, and memories (migration 0040). Rows marked
/// <c>embedded = false</c> are what is left to do; the indexer reads them, asks the model and saves the vectors.
/// The vector tables exist only after <see cref="SetupAsync"/> answered <c>ready</c>.
/// </summary>
public sealed class EmbeddingStore(NpgsqlDataSource db)
{
    public const string Ready = "ready";
    public const string Unavailable = "unavailable";

    /// <summary>One kind of row: its table, how its text is read, and the vector table.</summary>
    public sealed record Kind(string Name, string Table, string IdType, string TextSql, string VectorTable, string VectorKey);

    public static readonly Kind Segments = new("segment", "segments", "bigint", "s.text", "segment_embeddings", "segment_id");

    /// <summary>A conversation is its title (the user's, else the model's) and its summary.</summary>
    public static readonly Kind Conversations = new(
        "conversation", "conversations", "uuid",
        "nullif(concat_ws(E'\\n', coalesce(s.title, s.ai_title), s.ai_summary), '')", "conversation_embeddings", "conversation_id");

    /// <summary>A deleted memory loses its vector.</summary>
    public static readonly Kind Memories = new(
        "memory", "memories", "uuid", "case when s.deleted_at is null then s.text end", "memory_embeddings", "memory_id");

    public static readonly IReadOnlyList<Kind> Kinds = [Segments, Conversations, Memories];

    /// <summary>Runs <c>nytka_embeddings_setup</c>: <see cref="Ready"/> or <see cref="Unavailable"/>.</summary>
    public async Task<string> SetupAsync(string model, int dimensions, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "select nytka_embeddings_setup(@model, @dimensions)", new { model, dimensions }, commandTimeout: 0, cancellationToken: ct))
            ?? Unavailable;
    }

    /// <summary>Up to <paramref name="batch"/> rows of <paramref name="kind"/> still to embed, lowest id first.</summary>
    public async Task<IReadOnlyList<EmbeddingInput>> PendingAsync(Kind kind, int batch, CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        var sql = $"select s.id, {kind.TextSql} as text from {kind.Table} s where not s.embedded order by s.id limit @batch";
        await using var reader = await connection.ExecuteReaderAsync(new CommandDefinition(sql, new { batch }, cancellationToken: ct));
        var rows = new List<EmbeddingInput>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new EmbeddingInput(reader.GetValue(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>
    /// Saves the vectors (null: the row gets none, and loses an old one) and marks the rows done, but only the
    /// rows whose text is still what was embedded: one changed meanwhile stays to do. Returns how many it marked.
    /// </summary>
    public async Task<int> SaveAsync(Kind kind, IReadOnlyList<EmbeddingInput> rows, IReadOnlyList<float[]?> vectors, CancellationToken ct)
    {
        if (rows.Count != vectors.Count)
        {
            throw new ArgumentException("One vector per row.", nameof(vectors));
        }

        object ids = kind.IdType == "bigint" ? rows.Select(r => (long)r.Id).ToArray() : rows.Select(r => (Guid)r.Id).ToArray();
        var texts = rows.Select(r => r.Text).ToArray();
        var literals = vectors.Select(v => v is null ? null : Literal(v)).ToArray();

        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var sql = $"""
            with input as (
                select * from unnest(@ids::{kind.IdType}[], @texts::text[], @vectors::text[]) as u(id, text, vector)
            ),
            done as (
                update {kind.Table} s set embedded = true
                from input u
                where s.id = u.id and not s.embedded and {kind.TextSql} is not distinct from u.text
                returning s.id, u.vector
            ),
            dropped as (
                delete from {kind.VectorTable} e using done d where e.{kind.VectorKey} = d.id and d.vector is null
            ),
            saved as (
                insert into {kind.VectorTable} ({kind.VectorKey}, embedding)
                select d.id, d.vector::vector from done d where d.vector is not null
                on conflict ({kind.VectorKey}) do update set embedding = excluded.embedding
            )
            select count(*) from done
            """;
        var marked = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql, new { ids, texts, vectors = literals }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return marked;
    }

    /// <summary>How many rows are left to embed, of every kind.</summary>
    public async Task<long> PendingCountAsync(CancellationToken ct)
    {
        await using var connection = await db.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            select (select count(*) from segments where not embedded)
                 + (select count(*) from conversations where not embedded)
                 + (select count(*) from memories where not embedded)
            """,
            cancellationToken: ct));
    }

    /// <summary>pgvector's text form, <c>[0.1,0.2]</c>.</summary>
    public static string Literal(IReadOnlyList<float> vector)
    {
        var text = new StringBuilder("[");
        for (var i = 0; i < vector.Count; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        return text.Append(']').ToString();
    }
}
