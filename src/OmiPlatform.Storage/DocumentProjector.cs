using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using OmiPlatform.Omi;
using OmiPlatform.Omi.Models;

namespace OmiPlatform.Storage;

/// <summary>
/// Projects raw payloads into the typed tables (<c>conversations</c> + <c>action_items</c>,
/// <c>memories</c>).
/// </summary>
/// <remarks>
/// Every projection reads only from <see cref="OmiRawDocument.Payload"/>, never from the network,
/// so the whole warehouse can be rebuilt from <c>omi_raw</c> after a schema change at Omi's end —
/// the same invariant oura-platform enforces on <c>oura_raw</c>.
/// <para>
/// A document that fails to parse is logged and skipped rather than aborting the batch: the raw row
/// is already durable, and one unexpected shape must not stop the rest of a backfill.
/// </para>
/// </remarks>
public sealed class DocumentProjector
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DocumentProjector> _logger;

    public DocumentProjector(NpgsqlDataSource dataSource, ILogger<DocumentProjector> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<int> ProjectAsync(
        string docType,
        IReadOnlyList<OmiRawDocument> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return 0;
        }

        return docType switch
        {
            "conversation" => await ProjectConversationsAsync(documents, cancellationToken).ConfigureAwait(false),
            "memory" => await ProjectMemoriesAsync(documents, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(docType), docType, "Unknown doc type."),
        };
    }

    private async Task<int> ProjectConversationsAsync(
        IReadOnlyList<OmiRawDocument> documents,
        CancellationToken cancellationToken)
    {
        var projected = 0;

        // One transaction per conversation rather than per batch: an action-items re-fan-out
        // (delete then insert) has to see its own conversation row, and a batch-wide transaction
        // would hold that lock far longer than a personal-scale ingest needs to.
        foreach (var document in documents)
        {
            if (!TryParse<DeveloperConversation>(document, out var conversation))
            {
                continue;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var day = conversation.StartedAt is { } started ? DateOnly.FromDateTime(started.UtcDateTime) : (DateOnly?)null;

            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into conversations (
                    id, created_at, started_at, finished_at, day, language, source, folder_id, folder_name,
                    title, overview, category, emoji, transcript_text, transcript_segments, events, geolocation, updated_at)
                values (
                    @Id, @CreatedAt, @StartedAt, @FinishedAt, @Day, @Language, @Source, @FolderId, @FolderName,
                    @Title, @Overview, @Category, @Emoji, @TranscriptText, @TranscriptSegments::jsonb, @Events::jsonb, @Geolocation::jsonb, now())
                on conflict (id) do update set
                    created_at           = excluded.created_at,
                    started_at           = excluded.started_at,
                    finished_at          = excluded.finished_at,
                    day                  = excluded.day,
                    language             = excluded.language,
                    source               = excluded.source,
                    folder_id            = excluded.folder_id,
                    folder_name          = excluded.folder_name,
                    title                = excluded.title,
                    overview             = excluded.overview,
                    category             = excluded.category,
                    emoji                = excluded.emoji,
                    -- A reconcile pass without include_transcript=false never happens (the ingest
                    -- always asks for it), but coalesce guards against overwriting a good transcript
                    -- with a null from some future caller that does not.
                    transcript_text      = coalesce(excluded.transcript_text, conversations.transcript_text),
                    transcript_segments  = coalesce(excluded.transcript_segments, conversations.transcript_segments),
                    events               = excluded.events,
                    geolocation          = excluded.geolocation,
                    updated_at           = now()
                """,
                new
                {
                    conversation.Id,
                    conversation.CreatedAt,
                    conversation.StartedAt,
                    conversation.FinishedAt,
                    Day = day,
                    conversation.Language,
                    conversation.Source,
                    conversation.FolderId,
                    conversation.FolderName,
                    conversation.Structured.Title,
                    conversation.Structured.Overview,
                    conversation.Structured.Category,
                    conversation.Structured.Emoji,
                    TranscriptText = conversation.TranscriptSegments is { Count: > 0 } segments
                        ? string.Join(' ', segments.Select(s => s.Text))
                        : null,
                    TranscriptSegments = conversation.TranscriptSegments is null
                        ? null
                        : JsonSerializer.Serialize(conversation.TranscriptSegments, OmiJson.Options),
                    Events = JsonSerializer.Serialize(conversation.Structured.Events ?? [], OmiJson.Options),
                    Geolocation = conversation.Geolocation is null
                        ? null
                        : JsonSerializer.Serialize(conversation.Geolocation, OmiJson.Options),
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            // Action items carry no id of their own; re-fan every time rather than trying to
            // diff, since a conversation typically holds a handful.
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from action_items where conversation_id = @id",
                new { id = conversation.Id },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var actionItems = conversation.Structured.ActionItems ?? [];
            for (var index = 0; index < actionItems.Count; index++)
            {
                var item = actionItems[index];
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into action_items
                        (conversation_id, idx, description, completed, completed_at, due_at, created_at, updated_at)
                    values
                        (@ConversationId, @Idx, @Description, @Completed, @CompletedAt, @DueAt, @CreatedAt, @UpdatedAt)
                    """,
                    new
                    {
                        ConversationId = conversation.Id,
                        Idx = index,
                        item.Description,
                        item.Completed,
                        item.CompletedAt,
                        item.DueAt,
                        item.CreatedAt,
                        item.UpdatedAt,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            projected++;
        }

        return projected;
    }

    private async Task<int> ProjectMemoriesAsync(
        IReadOnlyList<OmiRawDocument> documents,
        CancellationToken cancellationToken)
    {
        var rows = new List<object>(documents.Count);

        foreach (var document in documents)
        {
            if (!TryParse<DeveloperMemory>(document, out var memory))
            {
                continue;
            }

            rows.Add(new
            {
                memory.Id,
                memory.Content,
                memory.Category,
                Tags = JsonSerializer.Serialize(memory.Tags ?? [], OmiJson.Options),
                memory.Visibility,
                memory.ManuallyAdded,
                memory.Reviewed,
                memory.Edited,
                memory.CreatedAt,
                memory.UpdatedAt,
            });
        }

        if (rows.Count == 0)
        {
            return 0;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into memories
                (id, content, category, tags, visibility, manually_added, reviewed, edited, created_at, updated_at)
            values
                (@Id, @Content, @Category, @Tags::jsonb, @Visibility, @ManuallyAdded, @Reviewed, @Edited, @CreatedAt, @UpdatedAt)
            on conflict (id) do update set
                content        = excluded.content,
                category       = excluded.category,
                tags           = excluded.tags,
                visibility     = excluded.visibility,
                manually_added = excluded.manually_added,
                reviewed       = excluded.reviewed,
                edited         = excluded.edited,
                created_at     = excluded.created_at,
                updated_at     = excluded.updated_at
            """,
            rows, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private bool TryParse<TDocument>(OmiRawDocument document, out TDocument parsed)
    {
        try
        {
            var value = JsonSerializer.Deserialize<TDocument>(document.Payload, OmiJson.Options);
            if (value is not null)
            {
                parsed = value;
                return true;
            }
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Could not project {DocType}/{DocId}; the raw payload is stored and can be replayed.",
                document.DocType,
                document.DocId);

            parsed = default!;
            return false;
        }

        _logger.LogWarning("Payload for {DocType}/{DocId} deserialized to null.", document.DocType, document.DocId);
        parsed = default!;
        return false;
    }
}
