using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>One line of the export (docs/specs/export.md). <see cref="Type"/> comes first in its JSON.</summary>
public abstract record ExportLine
{
    public abstract string Type { get; }
}

public sealed record ExportPerson(Guid Id, string Name, string? Note, bool Voiceprint, IReadOnlyList<string> Voices, IReadOnlyList<string> Tags, bool Named, DateTime CreatedAt) : ExportLine
{
    [JsonPropertyOrder(-1)]
    public override string Type => "person";
}

public sealed record ExportSegment(
    DateTime StartedAt, DateTime EndedAt, string Text, string? Speaker, string? SpeakerId, bool? IsUser, string? Person, string? SpeechKind,
    bool SpeechMarked);

public sealed record ExportConversation(
    Guid Id, string Source, string? ExternalId, DateTime StartedAt, DateTime EndedAt, string Status, string? Title, bool TitleEdited,
    string? Summary, IReadOnlyList<string> Tags, IReadOnlyList<ExportSegment> Segments) : ExportLine
{
    [JsonPropertyOrder(-1)]
    public override string Type => "conversation";
}

public sealed record ExportTask(
    Guid Id, Guid ConversationId, Guid? PersonId, string Text, bool Done, DateTime? DoneAt, DateTime CreatedAt, DateTime UpdatedAt) : ExportLine
{
    [JsonPropertyOrder(-1)]
    public override string Type => "task";
}

public sealed record ExportMemory(
    Guid Id, string Text, string Source, Guid? ConversationId, DateTime CreatedAt, DateTime UpdatedAt) : ExportLine
{
    [JsonPropertyOrder(-1)]
    public override string Type => "memory";
}

public sealed record ExportPersonFact(
    Guid Id, Guid PersonId, string Text, string Source, string? Basis, Guid? ConversationId, bool Edited,
    DateTime CreatedAt, DateTime UpdatedAt) : ExportLine
{
    [JsonPropertyOrder(-1)]
    public override string Type => "person_fact";
}

public sealed record ExportBookmark(Guid Id, DateTime At, string? Note, string Source, DateTime CreatedAt) : ExportLine
{
    [JsonPropertyOrder(-1)]
    public override string Type => "bookmark";
}

public sealed record ExportDigest(
    Guid Id, string LocalDate, string Headline, string Overview, IReadOnlyList<DigestHighlight> Highlights,
    IReadOnlyList<string> Decisions, IReadOnlyList<string> OpenQuestions, DateTime CreatedAt) : ExportLine
{
    [JsonPropertyOrder(-1)]
    public override string Type => "digest";
}

/// <summary>
/// Reads everything the user owns for the full export, as a stream of lines, from one repeatable-read snapshot. Rows come
/// off the reader one at a time; conversations are fetched <see cref="PageSize"/> at a time with their segments, so memory
/// stays bounded however long the history is. Soft-deleted tasks and memories are left out.
/// </summary>
public sealed class ExportStore(NpgsqlDataSource dataSource)
{
    public const int PageSize = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record ConversationRow(
        Guid Id, string Source, string? ExternalId, DateTime StartedAt, DateTime EndedAt, string Status, string? Title, bool TitleEdited, string? Summary);

    private sealed record SegmentRow(
        Guid ConversationId, DateTime StartedAt, DateTime EndedAt, string Text, string? Speaker, string? SpeakerId, bool? IsUser, string? Person,
        string? SpeechKind, bool SpeechMarked);

    private sealed record VoiceRow(Guid PersonId, string SpeakerId);

    private sealed record PersonRow(Guid Id, string Name, string? Note, bool Voiceprint, bool Named, DateTime CreatedAt);

    private sealed record DigestRaw(Guid Id, string LocalDate, string Headline, string Overview, string Body, DateTime CreatedAt);

    public async IAsyncEnumerable<ExportLine> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await connection.ExecuteAsync(new CommandDefinition("set transaction read only", transaction: transaction, cancellationToken: ct));

        var voices = (await connection.QueryAsync<VoiceRow>(new CommandDefinition(
            "select person_id as PersonId, speaker_id as SpeakerId from person_voices order by speaker_id", transaction: transaction, cancellationToken: ct)))
            .ToLookup(v => v.PersonId, v => v.SpeakerId);
        var people = (await connection.QueryAsync<PersonRow>(new CommandDefinition(
            """
            select id as Id, name as Name, note as Note, exists (select 1 from person_voiceprints v where v.person_id = people.id) as Voiceprint,
                   named as Named, created_at as CreatedAt
            from people order by lower(name), id
            """, transaction: transaction, cancellationToken: ct))).ToList();
        var personTags = await TagStore.OfPeopleAsync(connection, transaction, people.Select(p => p.Id).ToArray(), ct);
        foreach (var person in people)
        {
            yield return new ExportPerson(
                person.Id, person.Name, person.Note, person.Voiceprint, voices[person.Id].ToList(), personTags.GetValueOrDefault(person.Id) ?? [], person.Named, person.CreatedAt);
        }

        DateTime? afterAt = null;
        Guid? afterId = null;
        while (true)
        {
            var page = (await connection.QueryAsync<ConversationRow>(new CommandDefinition(
                """
                select id as Id, source as Source, external_id as ExternalId, started_at as StartedAt, ended_at as EndedAt,
                       status as Status, coalesce(title, ai_title) as Title, title is not null as TitleEdited, ai_summary as Summary
                from conversations
                where cast(@afterAt as timestamptz) is null or (started_at, id) > (cast(@afterAt as timestamptz), cast(@afterId as uuid))
                order by started_at, id
                limit @PageSize
                """,
                new { afterAt, afterId, PageSize }, transaction, cancellationToken: ct))).ToList();
            if (page.Count == 0)
            {
                break;
            }

            var ids = page.Select(c => c.Id).ToArray();
            var segments = (await connection.QueryAsync<SegmentRow>(new CommandDefinition(
                $"""
                select s.conversation_id as ConversationId, s.started_at as StartedAt, s.ended_at as EndedAt, s.text as Text,
                       s.speaker as Speaker, s.speaker_id as SpeakerId, {SpeakerLabel.IsUser} as IsUser, {SpeakerLabel.PersonName} as Person,
                       s.speech_kind as SpeechKind, s.speech_manual is not null as SpeechMarked
                from segments s {SpeakerLabel.Joins}
                where s.conversation_id = any(@ids)
                order by s.started_at, s.id
                """,
                new { ids }, transaction, cancellationToken: ct))).ToLookup(s => s.ConversationId);
            var tags = await TagStore.OfConversationsAsync(connection, transaction, ids, ct);
            foreach (var c in page)
            {
                yield return new ExportConversation(
                    c.Id, c.Source, c.ExternalId, c.StartedAt, c.EndedAt, c.Status, c.Title, c.TitleEdited, c.Summary,
                    tags.GetValueOrDefault(c.Id) ?? [],
                    segments[c.Id].Select(s => new ExportSegment(s.StartedAt, s.EndedAt, s.Text, s.Speaker, s.SpeakerId, s.IsUser, s.Person, s.SpeechKind, s.SpeechMarked)).ToList());
            }

            (afterAt, afterId) = (page[^1].StartedAt, page[^1].Id);
        }

        await foreach (var task in connection.QueryUnbufferedAsync<ExportTask>(
            """
            select id as Id, conversation_id as ConversationId, person_id as PersonId, text as Text, done as Done, done_at as DoneAt,
                   created_at as CreatedAt, updated_at as UpdatedAt
            from tasks where deleted_at is null order by created_at, id
            """, transaction: transaction).WithCancellation(ct))
        {
            yield return task;
        }

        await foreach (var memory in connection.QueryUnbufferedAsync<ExportMemory>(
            """
            select id as Id, text as Text, source as Source, conversation_id as ConversationId,
                   created_at as CreatedAt, updated_at as UpdatedAt
            from memories where deleted_at is null order by created_at, id
            """, transaction: transaction).WithCancellation(ct))
        {
            yield return memory;
        }

        await foreach (var fact in connection.QueryUnbufferedAsync<ExportPersonFact>(
            """
            select id as Id, person_id as PersonId, text as Text, source as Source, basis as Basis, conversation_id as ConversationId,
                   edited as Edited, created_at as CreatedAt, updated_at as UpdatedAt
            from person_facts where deleted_at is null order by created_at, id
            """, transaction: transaction).WithCancellation(ct))
        {
            yield return fact;
        }

        await foreach (var bookmark in connection.QueryUnbufferedAsync<ExportBookmark>(
            """
            select id as Id, at as At, note as Note, source as Source, created_at as CreatedAt
            from bookmarks order by at, id
            """, transaction: transaction).WithCancellation(ct))
        {
            yield return bookmark;
        }

        await foreach (var digest in connection.QueryUnbufferedAsync<DigestRaw>(
            """
            select id as Id, to_char(local_date, 'YYYY-MM-DD') as LocalDate, headline as Headline, overview as Overview,
                   body::text as Body, created_at as CreatedAt
            from digests order by local_date
            """, transaction: transaction).WithCancellation(ct))
        {
            var body = JsonSerializer.Deserialize<DigestBody>(digest.Body, Json)!;
            yield return new ExportDigest(digest.Id, digest.LocalDate, digest.Headline, digest.Overview, body.Highlights, body.Decisions, body.OpenQuestions, digest.CreatedAt);
        }
    }
}
