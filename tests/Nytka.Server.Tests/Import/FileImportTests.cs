using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Nytka.Audio.Decoding;
using Nytka.Audio.Playback;
using Nytka.Audio.Wav;
using Nytka.Server.Import;

namespace Nytka.Server.Tests.Import;

public sealed class FileImportTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private readonly NytkaApiFactory _factory = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Admin => _factory.CreateAuthorizedClient();

    private sealed record ConversationRow(string Source, string Status, string AiStatus, DateTime StartedAt, DateTime EndedAt);

    private Task<List<ConversationRow>> Conversations() => db.QueryAsync<ConversationRow>(
        "select source as Source, status as Status, ai_status as AiStatus, started_at as StartedAt, ended_at as EndedAt from conversations");

    private async Task<long> Count(string sql)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(sql);
    }

    [Fact]
    public async Task Txt_import_makes_one_conversation_with_one_segment()
    {
        var response = await Admin.PostAsync(
            "/api/v1/import/transcript?format=txt&startedAt=2026-03-01T09:00:00Z",
            new StringContent("Hello there.", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("segments").GetInt32());

        var rows = await Conversations();
        var row = Assert.Single(rows);
        Assert.Equal(("import", "closed", "done"), (row.Source, row.Status, row.AiStatus));
        Assert.Equal(new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), row.StartedAt);
        Assert.Equal(row.StartedAt, row.EndedAt);
    }

    [Fact]
    public async Task Srt_import_times_segments_from_the_start_time()
    {
        const string srt = """
            1
            00:00:01,000 --> 00:00:02,000
            Hello there.

            """;

        var response = await Admin.PostAsync(
            "/api/v1/import/transcript?format=srt&startedAt=2026-03-01T09:00:00Z",
            new StringContent(srt, Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var segments = await db.QueryAsync<(DateTime StartedAt, DateTime EndedAt, string Text)>(
            "select started_at as StartedAt, ended_at as EndedAt, text as Text from segments");
        var segment = Assert.Single(segments);
        Assert.Equal(new DateTime(2026, 3, 1, 9, 0, 1, DateTimeKind.Utc), segment.StartedAt);
        Assert.Equal(new DateTime(2026, 3, 1, 9, 0, 2, DateTimeKind.Utc), segment.EndedAt);
        Assert.Equal("Hello there.", segment.Text);
    }

    [Fact]
    public async Task Posting_the_same_file_twice_makes_two_conversations()
    {
        for (var i = 0; i < 2; i++)
        {
            await Admin.PostAsync(
                "/api/v1/import/transcript?format=txt&startedAt=2026-03-01T09:00:00Z",
                new StringContent("Hello.", Encoding.UTF8, "text/plain"));
        }

        Assert.Equal(2, await Count("select count(*) from conversations"));
    }

    [Theory]
    [InlineData("exe")]
    [InlineData(null)]
    public async Task An_unknown_format_is_rejected(string? format)
    {
        var query = format is null ? "" : $"&format={format}";
        var response = await Admin.PostAsync(
            $"/api/v1/import/transcript?startedAt=2026-03-01T09:00:00Z{query}",
            new StringContent("x", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-time")]
    [InlineData("2026-03-01T09:00:00")] // no offset
    [InlineData(null)]
    public async Task A_missing_or_offsetless_start_time_is_rejected(string? startedAt)
    {
        var query = startedAt is null ? "" : $"&startedAt={Uri.EscapeDataString(startedAt)}";
        var response = await Admin.PostAsync(
            $"/api/v1/import/transcript?format=txt{query}",
            new StringContent("x", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("1960-01-01T00:00:00Z")]  // before 1970
    [InlineData("9999-12-31T23:59:59Z")]  // past what a chunk's base time can hold
    public async Task A_start_time_outside_the_chunk_range_is_rejected_for_transcript_import_and_nothing_is_written(string startedAt)
    {
        var response = await Admin.PostAsync(
            $"/api/v1/import/transcript?format=txt&startedAt={Uri.EscapeDataString(startedAt)}",
            new StringContent("Hello there.", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Count("select count(*) from conversations"));
    }

    [Fact]
    public async Task A_malformed_srt_body_is_rejected_and_nothing_is_written()
    {
        var response = await Admin.PostAsync(
            "/api/v1/import/transcript?format=srt&startedAt=2026-03-01T09:00:00Z",
            new StringContent("not an srt file", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Count("select count(*) from conversations"));
    }

    [Fact]
    public async Task An_unauthorized_request_is_rejected()
    {
        var response = await _factory.CreateClient().PostAsync(
            "/api/v1/import/transcript?format=txt&startedAt=2026-03-01T09:00:00Z",
            new StringContent("x", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static short[] Tone(int samples) =>
        Enumerable.Range(0, samples)
            .Select(i => (short)(0.3 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / Timeline.SampleRate)))
            .ToArray();

    private static ByteArrayContent WavBody(short[] samples)
    {
        var content = new ByteArrayContent(WavWriter.Write(samples, Timeline.SampleRate));
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return content;
    }

    [Fact]
    public async Task Wav_import_stores_chunks_and_queues_processing()
    {
        var response = await Admin.PostAsync(
            "/api/v1/import/audio?startedAt=2026-03-01T09:00:00Z", WavBody(Tone(Timeline.SamplesPerFrame * 5)));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100, body.GetProperty("durationMs").GetInt32());
        Assert.Equal(1, await Count("select count(*) from audio_chunks"));
        Assert.Equal(1, await Count("select count(*) from jobs where kind = 'process-session'"));
    }

    [Fact]
    public async Task Ogg_opus_import_is_accepted()
    {
        var frames = AudioImportBuilder.Encode(Tone(Timeline.SamplesPerFrame * 5), new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var ogg = OggOpusWriter.Write([.. frames.Select(f => f.Payload)], serial: 1);
        var content = new ByteArrayContent(ogg);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");

        var response = await Admin.PostAsync("/api/v1/import/audio?startedAt=2026-03-01T09:00:00Z", content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, await Count("select count(*) from audio_chunks"));
    }

    [Theory]
    [InlineData("1960-01-01T00:00:00Z")]  // before 1970
    [InlineData("9999-12-31T23:59:59Z")]  // past what a chunk's base time can hold
    public async Task A_start_time_outside_the_chunk_range_is_rejected_for_audio_import_and_nothing_is_written(string startedAt)
    {
        var response = await Admin.PostAsync(
            $"/api/v1/import/audio?startedAt={Uri.EscapeDataString(startedAt)}", WavBody(Tone(Timeline.SamplesPerFrame * 5)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Count("select count(*) from audio_chunks"));
        Assert.Equal(0, await Count("select count(*) from jobs where kind = 'process-session'"));
    }

    [Fact]
    public async Task An_unsupported_media_type_is_rejected()
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");

        var response = await Admin.PostAsync("/api/v1/import/audio?startedAt=2026-03-01T09:00:00Z", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_wav_at_the_wrong_sample_rate_is_rejected()
    {
        var content = new ByteArrayContent(WavWriter.Write(Tone(800), sampleRate: 8000));
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        var response = await Admin.PostAsync("/api/v1/import/audio?startedAt=2026-03-01T09:00:00Z", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
