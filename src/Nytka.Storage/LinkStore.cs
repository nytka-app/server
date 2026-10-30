using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A conversation to cut into scenes: <paramref name="EndedAt"/> is null while it has no end.</summary>
public sealed record LinkConversation(Guid Id, DateTime StartedAt, DateTime? EndedAt);

/// <summary>
/// A line of a scene as the prompt sees it. <paramref name="Label"/> is the provider's label keyed by the request that made it (as
/// <see cref="TrackLine.Label"/>), <paramref name="TrackId"/> the track the line is in now.
/// </summary>
public sealed record SceneLine(long SegmentId, DateTime StartedAt, string? Label, Guid? TrackId, string Text);

/// <summary>A link to store: two letters of one scene are one person (<paramref name="Same"/>) or two.</summary>
public sealed record NewTrackLink(
    bool Same, double Confidence, IReadOnlyList<long> Segments, IReadOnlyList<long> OtherSegments, IReadOnlyList<long> Evidence);

/// <summary>A stored link, for the rebuild of its day.</summary>
public sealed record TrackLink(Guid Id, bool Same, double Confidence, IReadOnlyList<long> Segments, IReadOnlyList<long> OtherSegments);

/// <summary>How a scene's run ended: <c>done</c> (a model call), <c>skipped</c> (too little to ask) or <c>failed</c> (the attempts ran out).</summary>
public sealed record LinkRun(
    Guid ConversationId, DateTime SceneStartedAt, DateOnly Day, string Status, int Letters, int PromptChars, int AnswerChars);

/// <summary>
/// Links by meaning (<c>track_links</c> and <c>track_link_runs</c>, docs/explanation/features.md, Speaker tracks): the lines of a scene
/// for the prompt, the links the model found and the record of each scene asked.
/// </summary>
public sealed class LinkStore(NpgsqlDataSource dataSource)
{
    public const string Done = "done";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
    public const string Meaning = "meaning";

    /// <summary>The conversations that started in [<paramref name="from"/>, <paramref name="to"/>), oldest first.</summary>
    public async Task<IReadOnlyList<LinkConversation>> ConversationsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<LinkConversation>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, ended_at as EndedAt
            from conversations where started_at >= @from and started_at < @to
            order by started_at, id
            """,
            new { from, to }, cancellationToken: ct))).ToList();
    }

    /// <summary>The scenes among <paramref name="scenes"/> (conversation, start) that have a run.</summary>
    public async Task<IReadOnlySet<(Guid Conversation, DateTime Start)>> RunsAsync(Guid[] conversations, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<(Guid ConversationId, DateTime StartedAt)>(new CommandDefinition(
            "select conversation_id, scene_started_at from track_link_runs where conversation_id = any(@conversations)",
            new { conversations }, cancellationToken: ct));
        return rows.Select(r => (r.ConversationId, r.StartedAt)).ToHashSet();
    }

    public async Task<bool> HasRunAsync(Guid conversation, DateTime sceneStartedAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from track_link_runs where conversation_id = @conversation and scene_started_at = @sceneStartedAt)",
            new { conversation, sceneStartedAt }, cancellationToken: ct));
    }

    /// <summary>
    /// The lines of a scene that may be shown to the model, in time order: the conversation's lines from <paramref name="from"/> to
    /// <paramref name="to"/> that are inside the local day [<paramref name="dayFrom"/>, <paramref name="dayTo"/>), not media, with text.
    /// A line of the next day is never in a prompt, so a link never reaches across days.
    /// </summary>
    public async Task<IReadOnlyList<SceneLine>> SceneLinesAsync(
        Guid conversation, DateTime from, DateTime? to, DateTime dayFrom, DateTime dayTo, CancellationToken ct)
    {
        var start = from > dayFrom ? from : dayFrom;
        var end = to is { } t && t < dayTo ? t : dayTo;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<SceneLine>(new CommandDefinition(
            """
            select s.id as SegmentId, s.started_at as StartedAt,
                   case when nullif(btrim(s.speaker), '') is not null
                        then coalesce('p' || s.pass_id::text, 'b' || s.batch_id::text) || ':' || btrim(s.speaker) end as Label,
                   s.track_id as TrackId, s.text as Text
            from segments s
            where s.conversation_id = @conversation and s.started_at >= @start and s.started_at < @end
              and s.speech_kind is distinct from 'media' and btrim(s.text) <> ''
            order by s.started_at, s.id
            """,
            new { conversation, start, end }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Replaces the scene's links by <paramref name="links"/> and records the run, in one transaction. A scene asked again
    /// (after a paid pass, say) never keeps an old answer next to a new one.
    /// </summary>
    public async Task SaveAsync(LinkRun run, IReadOnlyList<NewTrackLink> links, DateTimeOffset now, long derivation, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from track_links where conversation_id = @ConversationId and scene_started_at = @SceneStartedAt",
            run, transaction, cancellationToken: ct));
        foreach (var link in links)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into track_links (id, day, conversation_id, scene_started_at, kind, source, confidence, segments, other_segments, evidence, created_at, derivation_id)
                values (@id, @Day, @ConversationId, @SceneStartedAt, @kind, @source, @Confidence, @segments, @otherSegments, @evidence, @now, @derivation)
                """,
                new
                {
                    id = Guid.NewGuid(), run.Day, run.ConversationId, run.SceneStartedAt, kind = link.Same ? TrackStore.AnswerSame : TrackStore.AnswerDifferent,
                    source = Meaning, link.Confidence, segments = link.Segments.ToArray(), otherSegments = link.OtherSegments.ToArray(),
                    evidence = link.Evidence.ToArray(), now, derivation,
                },
                transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into track_link_runs (conversation_id, scene_started_at, day, status, letters, pairs, prompt_chars, answer_chars, at, derivation_id)
            values (@ConversationId, @SceneStartedAt, @Day, @Status, @Letters, @pairs, @PromptChars, @AnswerChars, @now, @derivation)
            on conflict (conversation_id, scene_started_at) do update set
                day = excluded.day, status = excluded.status, letters = excluded.letters, pairs = excluded.pairs,
                prompt_chars = excluded.prompt_chars, answer_chars = excluded.answer_chars, at = excluded.at,
                derivation_id = excluded.derivation_id
            """,
            new { run.ConversationId, run.SceneStartedAt, run.Day, run.Status, run.Letters, pairs = links.Count, run.PromptChars, run.AnswerChars, now, derivation },
            transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>The links of a local day, for its rebuild.</summary>
    public async Task<IReadOnlyList<TrackLink>> DayLinksAsync(DateOnly day, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<(Guid Id, string Kind, double Confidence, long[] Segments, long[] OtherSegments)>(new CommandDefinition(
            "select id, kind, confidence::float8, segments, other_segments from track_links where day = @day and source = @source order by created_at, id",
            new { day, source = Meaning }, cancellationToken: ct)))
            .Select(r => new TrackLink(r.Id, r.Kind == TrackStore.AnswerSame, r.Confidence, r.Segments, r.OtherSegments))
            .ToList();
    }

    /// <summary>How many <c>link-speakers</c> jobs of a local day are still queued, <paramref name="exceptJob"/> not counted.</summary>
    public async Task<long> PendingJobsAsync(string localDate, long exceptJob, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "select count(*) from jobs where kind = 'link-speakers' and payload ->> 'LocalDate' = @localDate and id <> @exceptJob",
            new { localDate, exceptJob }, cancellationToken: ct));
    }
}
