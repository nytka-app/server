using System.Data;
using System.Runtime.CompilerServices;
using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>One line of a conversation's transcript, already labelled (docs/specs/export.md, Obsidian export).</summary>
public sealed record ObsidianSegment(DateTime StartedAt, string Text, string Speaker);

/// <summary>A task of a conversation, as a checkbox.</summary>
public sealed record ObsidianTask(string Text, bool Done);

public sealed record ObsidianConversation(
    Guid Id, DateTime StartedAt, DateTime EndedAt, string? Title, string? Summary, IReadOnlyList<string> Tags,
    IReadOnlyList<string> People, IReadOnlyList<ObsidianTask> Tasks, IReadOnlyList<ObsidianSegment> Segments);

/// <summary>A conversation a person spoke in, for their note's linked conversations.</summary>
public sealed record ObsidianPersonConversation(Guid Id, string? Title, DateTime StartedAt);

public sealed record ObsidianPerson(
    Guid Id, string Name, IReadOnlyList<string> Tags, IReadOnlyList<string> Facts, IReadOnlyList<ObsidianPersonConversation> Conversations);

/// <summary>A task added by hand with no conversation (<c>tasks.conversation_id</c> nullable since #145), for the
/// Obsidian export's Tasks note.</summary>
public sealed record ObsidianStandaloneTask(string Text, bool Done, DateTime? Due, Guid? PersonId, string? PersonName);

/// <summary>Either a person, a conversation or the standalone tasks note, read for the Obsidian export.</summary>
public abstract record ObsidianEntry;

public sealed record ObsidianPersonEntry(ObsidianPerson Person) : ObsidianEntry;

public sealed record ObsidianConversationEntry(ObsidianConversation Conversation) : ObsidianEntry;

public sealed record ObsidianTasksEntry(IReadOnlyList<ObsidianStandaloneTask> Tasks) : ObsidianEntry;

/// <summary>
/// Reads the data for the Obsidian Markdown export (docs/specs/export.md, Obsidian export): one repeatable-read,
/// read-only transaction, like <see cref="ExportStore"/>. People (with their facts, tags and every conversation they
/// spoke in) are read once; conversations (with their segments, tags and tasks) are paged <see cref="PageSize"/> at a
/// time, so memory stays bounded however long the history is; tasks with no conversation (added by hand) are read
/// once, last, for the export's single Tasks note.
/// </summary>
public sealed class ObsidianExportStore(NpgsqlDataSource dataSource)
{
    public const int PageSize = 100;

    private sealed record PersonRow(Guid Id, string Name);

    private sealed record FactRow(Guid PersonId, string Text);

    private sealed record PersonConversationRow(Guid PersonId, Guid Id, string? Title, DateTime StartedAt);

    private sealed record ConversationRow(Guid Id, DateTime StartedAt, DateTime EndedAt, string? Title, string? Summary);

    private sealed record SegmentRow(Guid ConversationId, DateTime StartedAt, string Text, string Speaker, string? Person);

    private sealed record TaskRow(Guid ConversationId, string Text, bool Done);

    public async IAsyncEnumerable<ObsidianEntry> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await connection.ExecuteAsync(new CommandDefinition("set transaction read only", transaction: transaction, cancellationToken: ct));

        var people = (await connection.QueryAsync<PersonRow>(new CommandDefinition(
            "select id as Id, name as Name from people order by lower(name), id", transaction: transaction, cancellationToken: ct))).ToList();
        var personIds = people.Select(p => p.Id).ToArray();
        var tags = await TagStore.OfPeopleAsync(connection, transaction, personIds, ct);
        var facts = (await connection.QueryAsync<FactRow>(new CommandDefinition(
            """
            select person_id as PersonId, text as Text from person_facts
            where deleted_at is null and person_id = any(@personIds)
            order by created_at, id
            """,
            new { personIds }, transaction, cancellationToken: ct))).ToLookup(f => f.PersonId);
        var personConversations = (await connection.QueryAsync<PersonConversationRow>(new CommandDefinition(
            $"""
            select distinct {SpeakerLabel.PersonId} as PersonId, c.id as Id, coalesce(c.title, c.ai_title) as Title, c.started_at as StartedAt
            from segments s join conversations c on c.id = s.conversation_id {SpeakerLabel.Joins}
            where {SpeakerLabel.PersonId} = any(@personIds) and {SpeakerLabel.IsUser} is not true
            order by c.started_at desc, c.id desc
            """,
            new { personIds }, transaction, cancellationToken: ct))).ToLookup(c => c.PersonId);
        foreach (var person in people)
        {
            yield return new ObsidianPersonEntry(new ObsidianPerson(
                person.Id, person.Name, tags.GetValueOrDefault(person.Id) ?? [], facts[person.Id].Select(f => f.Text).ToList(),
                personConversations[person.Id].Select(c => new ObsidianPersonConversation(c.Id, c.Title, c.StartedAt)).ToList()));
        }

        DateTime? afterAt = null;
        Guid? afterId = null;
        while (true)
        {
            var page = (await connection.QueryAsync<ConversationRow>(new CommandDefinition(
                """
                select id as Id, started_at as StartedAt, ended_at as EndedAt, coalesce(title, ai_title) as Title, ai_summary as Summary
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
            var conversationTags = await TagStore.OfConversationsAsync(connection, transaction, ids, ct);
            var segments = (await connection.QueryAsync<SegmentRow>(new CommandDefinition(
                $"""
                select s.conversation_id as ConversationId, s.started_at as StartedAt, s.text as Text,
                       {SpeakerLabel.Column} as Speaker, {SpeakerLabel.PersonName} as Person
                from segments s {SpeakerLabel.Joins}
                where s.conversation_id = any(@ids)
                order by s.started_at, s.id
                """,
                new { ids }, transaction, cancellationToken: ct))).ToLookup(s => s.ConversationId);
            var tasks = (await connection.QueryAsync<TaskRow>(new CommandDefinition(
                """
                select conversation_id as ConversationId, text as Text, done as Done
                from tasks where deleted_at is null and conversation_id = any(@ids)
                order by created_at, id
                """,
                new { ids }, transaction, cancellationToken: ct))).ToLookup(t => t.ConversationId);
            foreach (var c in page)
            {
                var people2 = segments[c.Id].Select(s => s.Person).Where(p => p is not null).Select(p => p!).Distinct().Order().ToList();
                yield return new ObsidianConversationEntry(new ObsidianConversation(
                    c.Id, c.StartedAt, c.EndedAt, c.Title, c.Summary, conversationTags.GetValueOrDefault(c.Id) ?? [], people2,
                    tasks[c.Id].Select(t => new ObsidianTask(t.Text, t.Done)).ToList(),
                    segments[c.Id].Select(s => new ObsidianSegment(s.StartedAt, s.Text, s.Speaker)).ToList()));
            }

            (afterAt, afterId) = (page[^1].StartedAt, page[^1].Id);
        }

        var standaloneTasks = (await connection.QueryAsync<ObsidianStandaloneTask>(new CommandDefinition(
            """
            select t.text as Text, t.done as Done, t.due_at as Due, t.person_id as PersonId, p.name as PersonName
            from tasks t left join people p on p.id = t.person_id
            where t.deleted_at is null and t.conversation_id is null
            order by t.created_at, t.id
            """,
            transaction: transaction, cancellationToken: ct))).ToList();
        if (standaloneTasks.Count > 0)
        {
            yield return new ObsidianTasksEntry(standaloneTasks);
        }
    }
}
