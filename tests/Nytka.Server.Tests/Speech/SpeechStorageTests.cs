using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nytka.Storage;

namespace Nytka.Server.Tests.Speech;

/// <summary>What migration 0024 adds (docs/specs/speech-kind.md, Storage): the columns on segments, their constraints and <c>speech_state</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SpeechStorageTests(PostgresFixture db) : IAsyncLifetime
{
    private const string CheckViolation = "23514";

    private static readonly DateTime T0 = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Column(string DataType, bool Nullable);

    private sealed record State(short Id, string AppliedMode, float AppliedThreshold);

    private async Task Segment()
    {
        var conversation = Guid.CreateVersion7();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@conversation, @T0, @T0, 'closed', @T0, @T0)",
            new { conversation, T0 });
        var batch = await db.ScalarAsync<long>(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at)
            values (@conversation, @T0, @T0, 'done', '{}', @T0) returning id
            """,
            new { conversation, T0 });
        await db.ExecuteAsync(
            "insert into segments (conversation_id, batch_id, started_at, ended_at, text) values (@conversation, @batch, @T0, @T0, 'hello')",
            new { conversation, batch, T0 });
    }

    private static async Task<string> SqlStateOf(Func<Task> statement) =>
        (await Assert.ThrowsAsync<PostgresException>(statement)).SqlState;

    [Theory]
    [InlineData("speech_guess", "text")]
    [InlineData("speech_score", "real")]
    [InlineData("speech_signals", "ARRAY")]
    [InlineData("speech_version", "smallint")]
    [InlineData("speech_manual", "text")]
    [InlineData("speech_kind", "text")]
    public async Task Segments_gain_nullable_speech_columns(string column, string dataType)
    {
        var columns = await db.QueryAsync<Column>(
            """
            select data_type as DataType, is_nullable = 'YES' as Nullable from information_schema.columns
            where table_schema = 'public' and table_name = 'segments' and column_name = @column
            """,
            new { column });

        Assert.Equal(new Column(dataType, true), Assert.Single(columns));
    }

    [Fact]
    public async Task A_stored_segment_starts_with_no_guess_no_mark_and_no_kind()
    {
        await Segment();

        Assert.True(await db.ScalarAsync<bool>(
            """
            select speech_guess is null and speech_score is null and speech_signals is null and speech_version is null
               and speech_manual is null and speech_kind is null
            from segments
            """));
    }

    [Theory]
    [InlineData("speech_guess", "person", null)]
    [InlineData("speech_guess", "media", null)]
    [InlineData("speech_guess", "call", null)]
    [InlineData("speech_guess", "unsure", null)]
    [InlineData("speech_guess", "maybe", CheckViolation)]
    [InlineData("speech_manual", "person", null)]
    [InlineData("speech_manual", "media", null)]
    [InlineData("speech_manual", "call", null)]
    [InlineData("speech_manual", "unsure", CheckViolation)]
    [InlineData("speech_kind", "person", null)]
    [InlineData("speech_kind", "media", null)]
    [InlineData("speech_kind", "call", null)]
    [InlineData("speech_kind", "unsure", CheckViolation)]
    public async Task A_guess_takes_four_values_and_a_mark_or_kind_three(string column, string value, string? sqlState)
    {
        await Segment();

        if (sqlState is null)
        {
            await db.ExecuteAsync($"update segments set {column} = @value", new { value });
            Assert.Equal(value, await db.ScalarAsync<string>($"select {column} from segments"));
        }
        else
        {
            Assert.Equal(sqlState, await SqlStateOf(() => db.ExecuteAsync($"update segments set {column} = @value", new { value })));
        }
    }

    [Theory]
    [InlineData(0f, null)]
    [InlineData(0.65f, null)]
    [InlineData(1f, null)]
    [InlineData(-0.1f, CheckViolation)]
    [InlineData(1.5f, CheckViolation)]
    public async Task A_score_is_from_0_to_1(float score, string? sqlState)
    {
        await Segment();

        if (sqlState is null)
        {
            await db.ExecuteAsync("update segments set speech_score = @score", new { score });
            Assert.Equal(score, await db.ScalarAsync<float>("select speech_score from segments"));
        }
        else
        {
            Assert.Equal(sqlState, await SqlStateOf(() => db.ExecuteAsync("update segments set speech_score = @score", new { score })));
        }
    }

    [Fact]
    public async Task Signals_are_a_list_of_text()
    {
        await Segment();

        await db.ExecuteAsync("update segments set speech_signals = array['far', 'turn']");

        Assert.Equal(["far", "turn"], await db.ScalarAsync<string[]>("select speech_signals from segments"));
    }

    [Fact]
    public async Task Media_lines_are_indexed_by_conversation()
    {
        var definition = await db.ScalarAsync<string>("select indexdef from pg_indexes where indexname = 'segments_speech_media'");

        Assert.Contains("(conversation_id)", definition, StringComparison.Ordinal);
        Assert.Contains("speech_kind = 'media'", definition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Speech_state_is_one_row_whose_id_is_1_and_whose_mode_is_one_of_three()
    {
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from speech_state"));
        Assert.Equal(
            CheckViolation,
            await SqlStateOf(() => db.ExecuteAsync("insert into speech_state (id, applied_mode, applied_threshold, updated_at) values (2, 'shadow', 0.8, now())")));
        Assert.Equal(CheckViolation, await SqlStateOf(() => db.ExecuteAsync("update speech_state set applied_mode = 'maybe'")));
        await db.ExecuteAsync("update speech_state set applied_mode = 'off'");
        await db.ExecuteAsync("update speech_state set applied_mode = 'on'");
    }

    [Fact]
    public async Task A_new_database_starts_in_shadow_at_the_default_threshold()
    {
        var fresh = new NpgsqlConnectionStringBuilder(db.ConnectionStringFor("speech_fresh")) { Pooling = false }.ConnectionString;
        await using (var admin = await db.DataSource.OpenConnectionAsync())
        {
            await admin.ExecuteAsync("drop database if exists speech_fresh");
        }

        new DatabaseMigrator(fresh, NullLogger<DatabaseMigrator>.Instance).Run();

        await using var connection = new NpgsqlConnection(fresh);
        var rows = await connection.QueryAsync<State>(
            "select id as Id, applied_mode as AppliedMode, applied_threshold as AppliedThreshold from speech_state");
        Assert.Equal(new State(1, "shadow", 0.8f), Assert.Single(rows));
    }
}
