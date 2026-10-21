using System.Text.Json;
using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>What one restore wrote (docs/specs/export.md, Re-importing). A kind with nothing in the file counts zero both ways.</summary>
public sealed record RestoreResult(
    ImportCounts People, ImportCounts Conversations, int Segments, ImportCounts Tasks, ImportCounts Memories, ImportCounts PersonFacts,
    ImportCounts Bookmarks, ImportCounts Digests);

/// <summary>
/// Writes Nytka's own export back (docs/specs/export.md, Re-importing) in one transaction, so a file that turns out cut or
/// malformed halfway writes nothing. Every record keeps its id; one whose id is already here, or whose natural key is (a
/// person's name, a conversation's <c>source</c>/<c>external_id</c>, a task's or memory's fingerprint), is skipped and left
/// as it is, so a second import of the same file changes nothing. Rows go in directly, as the Omi import's do: no event is
/// published and no job queued, and every restored conversation is marked as read by enrichment through its last segment.
/// </summary>
public sealed class RestoreStore(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <paramref name="lines"/> must come in the export's order (people before the conversations and records that name
    /// them); <paramref name="fingerprint"/> is the identity tasks, memories and facts match on. A throw from the
    /// enumeration rolls everything back.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(
        IAsyncEnumerable<ExportLine> lines, Func<string, string> fingerprint, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var run = new Run(connection, transaction, fingerprint, now, ct);

        await foreach (var line in lines.WithCancellation(ct))
        {
            switch (line)
            {
                case ExportPerson person:
                    await run.PersonAsync(person);
                    break;
                case ExportConversation conversation:
                    await run.ConversationAsync(conversation);
                    break;
                case ExportTask task:
                    await run.TaskAsync(task);
                    break;
                case ExportMemory memory:
                    await run.MemoryAsync(memory);
                    break;
                case ExportPersonFact fact:
                    await run.FactAsync(fact);
                    break;
                case ExportBookmark bookmark:
                    run.Bookmarks = Count(run.Bookmarks, await run.ExecuteAsync(
                        """
                        insert into bookmarks (id, at, note, source, created_at) values (@Id, @At, @Note, @Source, @CreatedAt)
                        on conflict do nothing
                        """,
                        bookmark));
                    break;
                case ExportDigest digest:
                    run.Digests = Count(run.Digests, await run.ExecuteAsync(
                        """
                        insert into digests (id, local_date, headline, overview, body, created_at)
                        values (@Id, cast(@LocalDate as date), @Headline, @Overview, cast(@body as jsonb), @CreatedAt)
                        on conflict do nothing
                        """,
                        new
                        {
                            digest.Id, digest.LocalDate, digest.Headline, digest.Overview, digest.CreatedAt,
                            body = JsonSerializer.Serialize(new DigestBody(digest.Highlights, digest.Decisions, digest.OpenQuestions), Json),
                        }));
                    break;
            }
        }

        await transaction.CommitAsync(ct);
        return new RestoreResult(
            run.People, run.Conversations, run.Segments, run.Tasks, run.Memories, run.PersonFacts, run.Bookmarks, run.Digests);
    }

    private static ImportCounts Count(ImportCounts counts, int inserted) =>
        new(counts.Imported + inserted, counts.Skipped + 1 - inserted);

    private sealed class Run(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Func<string, string> fingerprint, DateTimeOffset now, CancellationToken ct)
    {
        private static readonly ImportCounts None = new(0, 0);

        /// <summary>The file's person ids, and its names (lower-cased), to the ids they have here.</summary>
        private readonly Dictionary<Guid, Guid> _people = [];

        private readonly Dictionary<string, Guid> _names = new(StringComparer.Ordinal);

        /// <summary>Which person each provider voice id is named after here; read once, before the first conversation.</summary>
        private Dictionary<string, Guid>? _voices;

        public ImportCounts People { get; private set; } = None;

        public ImportCounts Conversations { get; private set; } = None;

        public int Segments { get; private set; }

        public ImportCounts Tasks { get; private set; } = None;

        public ImportCounts Memories { get; private set; } = None;

        public ImportCounts PersonFacts { get; private set; } = None;

        public ImportCounts Bookmarks { get; set; } = None;

        public ImportCounts Digests { get; set; } = None;

        public Task<int> ExecuteAsync(string sql, object args) =>
            connection.ExecuteAsync(new CommandDefinition(sql, args, transaction, cancellationToken: ct));

        private Task<T?> ScalarAsync<T>(string sql, object args) =>
            connection.ExecuteScalarAsync<T?>(new CommandDefinition(sql, args, transaction, cancellationToken: ct));

        /// <summary>A person whose id or name is already here maps to that one and is left as it is.</summary>
        public async Task PersonAsync(ExportPerson person)
        {
            var existing = await ScalarAsync<Guid?>(
                "select id from people where id = @Id or lower(name) = lower(@Name) order by id = @Id desc limit 1", new { person.Id, person.Name });
            var id = existing ?? person.Id;
            _people[person.Id] = id;
            _names[person.Name.ToLowerInvariant()] = id;
            if (existing is not null)
            {
                People = Count(People, 0);
                return;
            }

            await ExecuteAsync(
                "insert into people (id, name, note, named, created_at) values (@Id, @Name, @Note, @Named, @CreatedAt)", person);
            foreach (var voice in person.Voices)
            {
                await ExecuteAsync(
                    "insert into person_voices (speaker_id, person_id, created_at) values (@voice, @id, @now) on conflict do nothing",
                    new { voice, id, now });
            }

            foreach (var tag in person.Tags)
            {
                await TagStore.AddToPersonAsync(connection, transaction, id, tag, now, ct);
            }

            People = Count(People, 1);
        }

        /// <summary>
        /// A conversation whose id, or whose <c>source</c>/<c>external_id</c> pair, is already here is left as it is. A segment
        /// keeps the provider's label and voice id; its person is set on it only when its voice is not already named after them.
        /// </summary>
        public async Task ConversationAsync(ExportConversation conversation)
        {
            if (await ScalarAsync<bool>(
                    """
                    select exists (select 1 from conversations
                                   where id = @Id or (@ExternalId is not null and source = @Source and external_id = @ExternalId))
                    """,
                    new { conversation.Id, conversation.Source, conversation.ExternalId }))
            {
                Conversations = Count(Conversations, 0);
                return;
            }

            _voices ??= (await connection.QueryAsync<(string SpeakerId, Guid PersonId)>(new CommandDefinition(
                "select speaker_id, person_id from person_voices", transaction: transaction, cancellationToken: ct)))
                .ToDictionary(v => v.SpeakerId, v => v.PersonId, StringComparer.Ordinal);

            await ExecuteAsync(
                """
                insert into conversations (id, started_at, ended_at, status, title, ai_title, ai_summary, ai_status, ai_updated_at,
                                           source, external_id, created_at, updated_at)
                values (@Id, @StartedAt, @EndedAt, @Status, @title, @aiTitle, @Summary, 'done', @now, @Source, @ExternalId, @now, @now)
                """,
                new
                {
                    conversation.Id, conversation.StartedAt, conversation.EndedAt, conversation.Status, conversation.Summary,
                    conversation.Source, conversation.ExternalId, now,
                    title = conversation.TitleEdited ? conversation.Title : null,
                    aiTitle = conversation.TitleEdited ? null : conversation.Title,
                });

            var segments = conversation.Segments;
            var throughSegmentId = await ScalarAsync<long?>(
                """
                with rows as (
                    insert into segments (conversation_id, started_at, ended_at, text, speaker, speaker_id, is_user, person_id,
                                          speech_kind, speech_manual)
                    select @Id, s.started_at, s.ended_at, s.text, s.speaker, s.speaker_id, s.is_user, s.person_id, s.speech_kind,
                           case when s.speech_marked then s.speech_kind end
                    from unnest(@starts, @ends, @texts, @speakers, @speakerIds, @isUsers, @personIds, @kinds, @marked) with ordinality
                         as s(started_at, ended_at, text, speaker, speaker_id, is_user, person_id, speech_kind, speech_marked, ord)
                    order by s.ord
                    returning id)
                select max(id) from rows
                """,
                new
                {
                    conversation.Id,
                    starts = segments.Select(s => s.StartedAt).ToArray(),
                    ends = segments.Select(s => s.EndedAt).ToArray(),
                    texts = segments.Select(s => s.Text).ToArray(),
                    speakers = segments.Select(s => s.Speaker).ToArray(),
                    speakerIds = segments.Select(s => s.SpeakerId).ToArray(),
                    isUsers = segments.Select(s => s.IsUser).ToArray(),
                    personIds = segments.Select(SegmentPerson).ToArray(),
                    kinds = segments.Select(s => s.SpeechKind).ToArray(),
                    marked = segments.Select(s => s.SpeechMarked).ToArray(),
                });
            Segments += segments.Count;

            // Read through the last segment, so enrichment does not queue itself for what the file already carries.
            await ExecuteAsync(
                "update conversations set ai_through_segment_id = @throughSegmentId where id = @Id", new { conversation.Id, throughSegmentId });

            foreach (var tag in conversation.Tags)
            {
                await TagStore.AddToConversationAsync(connection, transaction, conversation.Id, tag, now, ct);
            }

            Conversations = Count(Conversations, 1);
        }

        private Guid? SegmentPerson(ExportSegment segment)
        {
            if (segment.Person is null || !_names.TryGetValue(segment.Person.ToLowerInvariant(), out var person))
            {
                return null;
            }

            return segment.SpeakerId is { } voice && _voices!.TryGetValue(voice, out var named) && named == person ? null : person;
        }

        private async Task<Guid?> PersonAsync(Guid? id) =>
            id is not { } fileId ? null
            : _people.TryGetValue(fileId, out var mapped) ? mapped
            : await ScalarAsync<Guid?>("select id from people where id = @fileId", new { fileId });

        /// <summary>A task whose conversation is not here is skipped: it must not turn into one added by hand.</summary>
        public async Task TaskAsync(ExportTask task)
        {
            if (task.ConversationId is { } conversation
                && !await ScalarAsync<bool>("select exists (select 1 from conversations where id = @conversation)", new { conversation }))
            {
                Tasks = Count(Tasks, 0);
                return;
            }

            Tasks = Count(Tasks, await ExecuteAsync(
                """
                insert into tasks (id, conversation_id, person_id, text, fingerprint, done, done_at, kind, due_at, created_at, updated_at)
                values (@Id, @ConversationId, @personId, @Text, @fingerprint, @Done, @DoneAt, @Kind, @Due, @CreatedAt, @UpdatedAt)
                on conflict do nothing
                """,
                new
                {
                    task.Id, task.ConversationId, task.Text, task.Done, task.DoneAt, task.Kind, task.Due, task.CreatedAt, task.UpdatedAt,
                    personId = await PersonAsync(task.PersonId), fingerprint = fingerprint(task.Text),
                }));
        }

        public async Task MemoryAsync(ExportMemory memory) =>
            Memories = Count(Memories, await ExecuteAsync(
                """
                insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at)
                values (@Id, @Text, @fingerprint, @Source, (select id from conversations where id = @ConversationId), @CreatedAt, @UpdatedAt)
                on conflict do nothing
                """,
                new { memory.Id, memory.Text, memory.Source, memory.ConversationId, memory.CreatedAt, memory.UpdatedAt, fingerprint = fingerprint(memory.Text) }));

        /// <summary>A fact about a person who is not here is skipped.</summary>
        public async Task FactAsync(ExportPersonFact fact)
        {
            if (await PersonAsync(fact.PersonId) is not { } person)
            {
                PersonFacts = Count(PersonFacts, 0);
                return;
            }

            PersonFacts = Count(PersonFacts, await ExecuteAsync(
                """
                insert into person_facts (id, person_id, text, fingerprint, source, basis, conversation_id, edited, created_at, updated_at)
                values (@Id, @person, @Text, @fingerprint, @Source, @Basis, (select id from conversations where id = @ConversationId), @Edited,
                        @CreatedAt, @UpdatedAt)
                on conflict do nothing
                """,
                new
                {
                    fact.Id, person, fact.Text, fact.Source, fact.Basis, fact.ConversationId, fact.Edited, fact.CreatedAt, fact.UpdatedAt,
                    fingerprint = fingerprint(fact.Text),
                }));
        }
    }
}
