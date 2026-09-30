using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed record ImportSegment(DateTimeOffset StartedAt, DateTimeOffset EndedAt, string Text, string? Speaker, bool? IsUser);

/// <summary>A task with the fingerprint its text has as AI output; <paramref name="CreatedAt"/> orders it among the others.</summary>
public sealed record ImportTask(string Text, string Fingerprint, bool Done, DateTimeOffset CreatedAt);

public sealed record ImportConversation(
    string ExternalId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string? Title, string? Summary,
    IReadOnlyList<ImportSegment> Segments, IReadOnlyList<ImportTask> Tasks);

/// <summary>A task listed outside a conversation; it is kept when its conversation is imported or already was.</summary>
public sealed record ImportLooseTask(string? ConversationExternalId, ImportTask Task);

public sealed record ImportMemory(string Text, string Fingerprint, DateTimeOffset CreatedAt, string? ConversationExternalId);

/// <summary>What a parsed export holds. <paramref name="Discarded"/> and <paramref name="Empty"/> count conversations left out.</summary>
public sealed record ImportBatch(
    int Discarded, int Empty, IReadOnlyList<ImportConversation> Conversations, IReadOnlyList<ImportLooseTask> LooseTasks,
    IReadOnlyList<ImportMemory> Memories);

public sealed record ImportCounts(int Imported, int Skipped);

public sealed record ConversationCounts(int Imported, int AlreadyImported, int Overlapping, int Discarded, int Empty);

public sealed record ImportResult(ConversationCounts Conversations, int Segments, ImportCounts Tasks, ImportCounts Memories);

/// <summary>
/// Writes an Omi export (<c>conversations.source = 'omi'</c>) in one transaction. A conversation that overlaps one
/// of Nytka's own in time is left out and counted, unless <c>importOverlapping</c>: the same talk may have been
/// captured by both.
/// </summary>
public sealed class ImportStore(NpgsqlDataSource dataSource)
{
    public const string OmiSource = "omi";

    public async Task<ImportResult> ImportAsync(ImportBatch batch, bool importOverlapping, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var already = 0;
        var overlapping = 0;
        var imported = 0;
        var segments = 0;
        var taskCounts = new ImportCounts(0, 0);

        foreach (var conversation in batch.Conversations)
        {
            var existing = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "select id from conversations where source = @source and external_id = @ExternalId",
                new { source = OmiSource, conversation.ExternalId }, transaction, cancellationToken: ct));
            if (existing is { } known)
            {
                ids.TryAdd(conversation.ExternalId, known);
                already++;
                continue;
            }

            if (!importOverlapping && await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    """
                    select exists (select 1 from conversations
                                   where source = 'nytka' and started_at < @EndedAt and ended_at > @StartedAt)
                    """,
                    new { conversation.StartedAt, conversation.EndedAt }, transaction, cancellationToken: ct)))
            {
                overlapping++;
                continue;
            }

            var id = Guid.CreateVersion7(conversation.StartedAt);
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into conversations (id, started_at, ended_at, status, ai_title, ai_summary, ai_status, ai_updated_at,
                                           source, external_id, created_at, updated_at)
                values (@id, @StartedAt, @EndedAt, 'closed', @Title, @Summary, 'done', @now, @source, @ExternalId, @now, @now)
                on conflict (source, external_id) do nothing
                """,
                new { id, conversation.StartedAt, conversation.EndedAt, conversation.Title, conversation.Summary, conversation.ExternalId, source = OmiSource, now },
                transaction, cancellationToken: ct));
            if (inserted == 0)
            {
                already++;
                continue;
            }

            ids[conversation.ExternalId] = id;
            imported++;
            var throughSegmentId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                with rows as (
                    insert into segments (conversation_id, started_at, ended_at, text, speaker, is_user)
                    select @id, s.started_at, s.ended_at, s.text, s.speaker, s.is_user
                    from unnest(@starts, @ends, @texts, @speakers, @isUsers) with ordinality
                         as s(started_at, ended_at, text, speaker, is_user, ord)
                    order by s.ord
                    returning id)
                select max(id) from rows
                """,
                new
                {
                    id,
                    starts = conversation.Segments.Select(s => s.StartedAt.UtcDateTime).ToArray(),
                    ends = conversation.Segments.Select(s => s.EndedAt.UtcDateTime).ToArray(),
                    texts = conversation.Segments.Select(s => s.Text).ToArray(),
                    speakers = conversation.Segments.Select(s => s.Speaker).ToArray(),
                    isUsers = conversation.Segments.Select(s => s.IsUser).ToArray(),
                },
                transaction, cancellationToken: ct));
            segments += conversation.Segments.Count;

            // The run is recorded as read through the last segment, so enrichment does not queue itself again.
            await connection.ExecuteAsync(new CommandDefinition(
                "update conversations set ai_through_segment_id = @throughSegmentId where id = @id",
                new { id, throughSegmentId }, transaction, cancellationToken: ct));

            taskCounts = Add(taskCounts, await InsertTasksAsync(connection, transaction, id, conversation.Tasks, now, ct));
        }

        foreach (var loose in batch.LooseTasks)
        {
            taskCounts = Add(
                taskCounts,
                loose.ConversationExternalId is { } external && ids.TryGetValue(external, out var id)
                    ? await InsertTasksAsync(connection, transaction, id, [loose.Task], now, ct)
                    : new ImportCounts(0, 1));
        }

        var memoryCounts = new ImportCounts(0, 0);
        foreach (var memory in batch.Memories)
        {
            if (memory.Fingerprint.Length == 0)
            {
                memoryCounts = Add(memoryCounts, new ImportCounts(0, 1));
                continue;
            }

            Guid? conversationId = memory.ConversationExternalId is { } external && ids.TryGetValue(external, out var linked)
                ? linked
                : null;
            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at)
                values (@id, @Text, @Fingerprint, 'omi', @conversationId, @CreatedAt, @CreatedAt)
                on conflict (fingerprint) do nothing
                """,
                new { id = Guid.CreateVersion7(memory.CreatedAt), memory.Text, memory.Fingerprint, conversationId, memory.CreatedAt },
                transaction, cancellationToken: ct));
            memoryCounts = Add(memoryCounts, new ImportCounts(inserted, 1 - inserted));
        }

        await transaction.CommitAsync(ct);
        return new ImportResult(
            new ConversationCounts(imported, already, overlapping, batch.Discarded, batch.Empty), segments, taskCounts, memoryCounts);
    }

    private static async Task<ImportCounts> InsertTasksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, IReadOnlyList<ImportTask> tasks,
        DateTimeOffset now, CancellationToken ct)
    {
        var counts = new ImportCounts(0, 0);
        foreach (var task in tasks)
        {
            if (task.Fingerprint.Length == 0)
            {
                counts = Add(counts, new ImportCounts(0, 1));
                continue;
            }

            var inserted = await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into tasks (id, conversation_id, text, fingerprint, done, done_at, created_at, updated_at)
                values (@id, @conversationId, @Text, @Fingerprint, @Done, case when @Done then @CreatedAt end, @CreatedAt, @CreatedAt)
                on conflict (conversation_id, fingerprint) do nothing
                """,
                new { id = Guid.CreateVersion7(task.CreatedAt), conversationId, task.Text, task.Fingerprint, task.Done, task.CreatedAt },
                transaction, cancellationToken: ct));
            counts = Add(counts, new ImportCounts(inserted, 1 - inserted));
        }

        return counts;
    }

    private static ImportCounts Add(ImportCounts a, ImportCounts b) => new(a.Imported + b.Imported, a.Skipped + b.Skipped);
}
