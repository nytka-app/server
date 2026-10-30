using Dapper;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>What <see cref="PersonForget"/> removed, per kind; <paramref name="Conversations"/> is the number queued for a new summary.</summary>
public sealed record ForgetCounts(
    int Lines, int Facts, int Memories, int Tasks, int Notes, int Voiceprints, int VoiceMatches, int VoiceGroups,
    int NameSuggestions, int Tags, int TagSuggestions, int TopicPages, int Events, int Briefs, int Conversations);

/// <summary>
/// Forgets a person completely (docs/reference/people.md): their lines lose the attribution, and everything built from
/// them or about them goes, in one transaction. A memory, task or note goes only when every line it cites is the
/// person's; the conversations and everyone else's lines stay.
/// </summary>
public sealed class PersonForget(NpgsqlDataSource dataSource, JobQueue queue, ILlmClient llm, TimeProvider time)
{
    public sealed record Result(ForgetCounts Counts, IReadOnlyList<string> Voices);

    /// <summary>Null when the person does not exist.</summary>
    public async Task<Result?> RunAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        if (await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "select id from people where id = @id for update", new { id }, transaction, cancellationToken: ct)) is null)
        {
            return null;
        }

        async Task<int> Run(string sql, object? args = null) =>
            await connection.ExecuteAsync(new CommandDefinition(sql, args ?? new { id }, transaction, cancellationToken: ct));

        var voices = (await connection.QueryAsync<string>(new CommandDefinition(
            "select speaker_id from person_voices where person_id = @id order by speaker_id", new { id }, transaction, cancellationToken: ct))).ToList();
        var lines = (await connection.QueryAsync<long>(new CommandDefinition(
            $"""
            select s.id from segments s {SpeakerLabel.Joins}
            where {SpeakerLabel.IsUser} is not true and {SpeakerLabel.PersonId} = @id
            """, new { id }, transaction, cancellationToken: ct))).ToArray();
        var conversations = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "select distinct conversation_id from segments where id = any(@lines)", new { lines }, transaction, cancellationToken: ct))).ToArray();

        // An item with sources whose every source is one of the person's lines; an item without sources is not theirs to forget.
        Task<int> Items(string table, string sources, string key) => Run(
            $"""
            delete from {table} t
            where exists (select 1 from {sources} x where x.{key} = t.id)
              and not exists (select 1 from {sources} x where x.{key} = t.id and not (x.segment_id = any(@lines)))
            """, new { lines });

        var memories = await Items("memories", "memory_sources", "memory_id");
        var tasks = await Items("tasks", "task_sources", "task_id");
        var notes = await Items("notes", "note_sources", "note_id");

        var facts = await Run("delete from person_facts where person_id = @id");
        var briefs = await Run("delete from briefs where @id = any (person_ids)");
        var events = await Run("delete from events where person_id = @id");
        var voiceMatches = await Run("delete from voice_matches where person_id = @id");
        var nameSuggestions = await Run("delete from name_suggestions where person_id = @id");
        var tags = await Run("delete from person_tags where person_id = @id");
        var tagSuggestions = await Run("delete from tag_suggestions where person_id = @id");
        var topicPages = await Run("delete from topics where person_id = @id");
        var voiceprints = await Run("delete from person_voiceprints where person_id = @id");

        // A group's centroid is a mean that holds the person's voice: its other fingerprints are grouped again.
        var groups = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "select distinct group_id from segment_fingerprints where segment_id = any(@lines) and group_id is not null",
            new { lines }, transaction, cancellationToken: ct))).ToArray();
        voiceprints += await Run("delete from segment_fingerprints where segment_id = any(@lines)", new { lines });
        await Run("update segment_fingerprints set grouped = false where group_id = any(@groups)", new { groups });
        var voiceGroups = await Run("delete from voice_groups where id = any(@groups)", new { groups });

        // A speaker track's centroid is a mean that holds the person's voice, as a group's does: the tracks of the person's lines go,
        // with the window fingerprints of those lines (the other lines of a track are clustered again by the next build).
        voiceprints += await Run(
            """
            delete from track_prints
            where segment_id = any(@lines)
               or segment_id in (select s.id from segments s join speaker_tracks t on t.id = s.track_id where t.person_id = @id)
            """,
            new { id, lines });
        voiceGroups += await Run(
            "delete from speaker_tracks where person_id = @id or id in (select track_id from segments where id = any(@lines) and track_id is not null)",
            new { id, lines });

        await Run("update segments set person_id = null where person_id = @id");
        await Run("delete from person_voices where person_id = @id");
        await Run("update related_moments set person_ids = array_remove(person_ids, @id) where @id = any (person_ids)");
        await Run(
            """
            update conversations set related_stale = true
            where not related_stale
              and (id = any(@conversations) or id in (select conversation_id from related_moments where earlier_id = any(@conversations)))
            """, new { conversations });

        var queued = 0;
        if (llm.IsConfigured)
        {
            foreach (var conversation in conversations)
            {
                // Only a closed conversation is summarized; the open one is when it closes.
                if (await Run(
                        "update conversations set ai_status = 'pending', ai_failures = 0 where id = @conversation and status = 'closed'",
                        new { conversation }) == 0)
                {
                    continue;
                }

                await queue.EnqueueAsync(
                    connection, transaction, JobKinds.EnrichConversation, new EnrichPayload(conversation, Force: true),
                    JobKinds.EnrichConversationKey(conversation), time.GetUtcNow(), ct);
                queued++;
            }
        }

        await Run("delete from people where id = @id");
        await transaction.CommitAsync(ct);
        return new Result(
            new ForgetCounts(
                lines.Length, facts, memories, tasks, notes, voiceprints, voiceMatches, voiceGroups, nameSuggestions, tags, tagSuggestions,
                topicPages, events, briefs, queued),
            voices);
    }
}
