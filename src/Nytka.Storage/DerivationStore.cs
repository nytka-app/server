using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// A layer's current version (<paramref name="Current"/>, the newest <c>derivation_versions</c> row) and its first
/// (<paramref name="First"/>), which a row written before versions existed counts as.
/// </summary>
public readonly record struct LayerVersion(long Current, long First);

/// <summary>The characters a rebuild would send to the model and the most it would get back.</summary>
public readonly record struct LayerChars(long Input, long Output);

/// <summary>
/// The versions of the derived layers and which days are stale for each (docs/explanation/features.md, Nightly re-think). A version is a
/// row of <c>derivation_versions</c>; a derived row carries the id of the version that made it, and a day is stale for a layer when one
/// of its rows carries an older id than the layer's current. Each layer has its own stale query, because each layer's rows live in its
/// own tables.
/// </summary>
public sealed class DerivationStore(NpgsqlDataSource dataSource)
{
    private sealed record VersionRow(long Id, string ModelHash, string PromptHash, string SettingsHash, string UpstreamHash);

    /// <summary>
    /// The layer's current version for these hashes: its newest row when all four match, else a new row. One transaction under an
    /// advisory lock per layer, so two callers that see a change at once add one row.
    /// </summary>
    public async Task<LayerVersion> ResolveAsync(
        string layer, string modelHash, string promptHash, string settingsHash, string upstreamHash, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(hashtext(@key))", new { key = "derivation:" + layer }, transaction, cancellationToken: ct));
        var latest = await connection.QueryFirstOrDefaultAsync<VersionRow>(new CommandDefinition(
            """
            select id as Id, model_hash as ModelHash, prompt_hash as PromptHash, settings_hash as SettingsHash, upstream_hash as UpstreamHash
            from derivation_versions where layer = @layer order by id desc limit 1
            """,
            new { layer }, transaction, cancellationToken: ct));
        var current = latest is not null && latest.ModelHash == modelHash && latest.PromptHash == promptHash
            && latest.SettingsHash == settingsHash && latest.UpstreamHash == upstreamHash
            ? latest.Id
            : await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                insert into derivation_versions (layer, model_hash, prompt_hash, settings_hash, upstream_hash, created_at)
                values (@layer, @modelHash, @promptHash, @settingsHash, @upstreamHash, @now) returning id
                """,
                new { layer, modelHash, promptHash, settingsHash, upstreamHash, now }, transaction, cancellationToken: ct));
        var first = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "select min(id) from derivation_versions where layer = @layer", new { layer }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return new LayerVersion(current, first);
    }

    /// <summary>
    /// The local days whose tracks an older version built, oldest first, and that can be built again: a track whose lines still have a
    /// fingerprint of <paramref name="model"/> (retention deletes them with the speech audio, and a day without them cannot change).
    /// </summary>
    public Task<IReadOnlyList<DateOnly>> StaleTrackDaysAsync(LayerVersion version, string model, CancellationToken ct) => DaysAsync(
        """
        select distinct t.day from speaker_tracks t
        where coalesce(t.derivation_id, @first) < @current
          and exists (
              select 1 from segments s
              where s.track_id = t.id
                and (exists (select 1 from track_prints p where p.segment_id = s.id and p.model = @model)
                     or exists (select 1 from segment_fingerprints f where f.segment_id = s.id and f.model = @model)))
        order by 1
        """,
        new { current = version.Current, first = version.First, model }, ct);

    /// <summary>The local days with a scene asked about by an older version, oldest first.</summary>
    public Task<IReadOnlyList<DateOnly>> StaleLinkDaysAsync(LayerVersion version, CancellationToken ct) => DaysAsync(
        "select distinct day from track_link_runs where coalesce(derivation_id, @first) < @current order by 1",
        new { current = version.Current, first = version.First }, ct);

    /// <summary>The local days named by an older version, oldest first.</summary>
    public Task<IReadOnlyList<DateOnly>> StaleNameDaysAsync(LayerVersion version, CancellationToken ct) => DaysAsync(
        "select day from track_name_runs where coalesce(derivation_id, @first) < @current order by 1",
        new { current = version.Current, first = version.First }, ct);

    /// <summary>The local days cut by an older version, oldest first.</summary>
    public Task<IReadOnlyList<DateOnly>> StaleSituationDaysAsync(LayerVersion version, CancellationToken ct) => DaysAsync(
        "select distinct day from situations where coalesce(derivation_id, @first) < @current order by 1",
        new { current = version.Current, first = version.First }, ct);

    /// <summary>The local days with a summary made by an older version, oldest first. Only a summary exists to be made again.</summary>
    public Task<IReadOnlyList<DateOnly>> StaleEnrichmentDaysAsync(LayerVersion version, CancellationToken ct) => DaysAsync(
        "select distinct day from situations where ai_status = 'done' and coalesce(ai_derivation_id, @first) < @current order by 1",
        new { current = version.Current, first = version.First }, ct);

    /// <summary>The local days (in <paramref name="zone"/>) with a conversation that is not closed: a rebuild waits for them.</summary>
    public Task<IReadOnlyList<DateOnly>> OpenDaysAsync(string zone, CancellationToken ct) => DaysAsync(
        "select distinct (started_at at time zone @zone)::date from conversations where status <> 'closed'", new { zone }, ct);

    /// <summary>What the scenes of the day cost when they were asked: the characters sent and received, from their runs.</summary>
    public async Task<LayerChars> LinkCharsAsync(DateOnly day, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var (input, output) = await connection.QuerySingleAsync<(long, long)>(new CommandDefinition(
            "select coalesce(sum(prompt_chars), 0)::bigint, coalesce(sum(answer_chars), 0)::bigint from track_link_runs where day = @day",
            new { day }, cancellationToken: ct));
        return new LayerChars(input, output);
    }

    /// <summary>
    /// What summaries of the day's stale situations would cost: the characters of their lines, and <paramref name="perSummary"/> (the
    /// prompt's, and the answer's longest) for each.
    /// </summary>
    public async Task<LayerChars> EnrichmentCharsAsync(DateOnly day, LayerVersion version, LayerChars perSummary, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var (input, count) = await connection.QuerySingleAsync<(long, long)>(new CommandDefinition(
            """
            select coalesce(sum(length(g.text)), 0)::bigint, count(distinct s.id)::bigint
            from situations s
            left join situation_segments ss on ss.situation_id = s.id
            left join segments g on g.id = ss.segment_id
            where s.day = @day and s.ai_status = 'done' and coalesce(s.ai_derivation_id, @first) < @current
            """,
            new { day, current = version.Current, first = version.First }, cancellationToken: ct));
        return new LayerChars(input + count * perSummary.Input, count * perSummary.Output);
    }

    /// <summary>
    /// Forgets that the day's scenes were asked, so that each is asked again. The links stay until a scene's new answer replaces
    /// them, and the owner's answers (<c>track_answers</c>) are not touched.
    /// </summary>
    public async Task ForgetLinkRunsAsync(DateOnly day, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("delete from track_link_runs where day = @day", new { day }, cancellationToken: ct));
    }

    /// <summary>
    /// Marks the day's stale summaries as made from no lines, so that the scan asks for each again; the title and summary stay until the
    /// new ones replace them. The ids of the situations marked.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> MarkSummariesStaleAsync(DateOnly day, LayerVersion version, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            update situations set ai_lines_hash = null
            where day = @day and ai_status = 'done' and coalesce(ai_derivation_id, @first) < @current
            returning id
            """,
            new { day, current = version.Current, first = version.First }, cancellationToken: ct))).ToList();
    }

    private async Task<IReadOnlyList<DateOnly>> DaysAsync(string sql, object args, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<DateOnly>(new CommandDefinition(sql, args, cancellationToken: ct))).OrderBy(d => d).ToList();
    }
}
