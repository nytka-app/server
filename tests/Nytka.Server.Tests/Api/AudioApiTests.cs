using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Nytka.Audio.Frames;
using Nytka.Server.Jobs;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class AudioApiTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Uploads speech, processes it and closes the conversation. Two stretches of tone 60 s apart make two runs.</summary>
    private async Task<Guid> Speak(params Part[] parts)
    {
        await _server.UploadAsync(Chunks(Guid.NewGuid(), parts));
        await _server.RunJobsAsync();
        _server.Time.Advance(TimeSpan.FromSeconds(61));
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
        return await db.ScalarAsync<Guid>("select id from conversations order by started_at limit 1");
    }

    private static long Ms(JsonElement time) => DateTimeOffset.Parse(time.GetString()!).ToUnixTimeMilliseconds();

    [Fact]
    public async Task Serves_an_Ogg_stream_of_the_stored_speech()
    {
        var id = await Speak(Tone(3), Silence(2));

        var response = await _server.CreateAuthorizedClient().GetAsync($"/api/v1/conversations/{id}/audio");

        response.EnsureSuccessStatusCode();
        Assert.Equal("audio/ogg", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("bytes", response.Headers.AcceptRanges);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.True(body.AsSpan(0, 4).SequenceEqual("OggS"u8));
        Assert.True(body.AsSpan(28, 8).SequenceEqual("OpusHead"u8));

        // The last page's granule is the stored packet count times 960.
        var packets = await db.QueryAsync<byte[]>("select body from speech_audio where conversation_id = @id", new { id });
        var frames = packets.Sum(p => ChunkFormat.Read(p).Frames.Count);
        var last = body.AsSpan().LastIndexOf("OggS"u8);
        Assert.Equal(4, body[last + 5]);
        Assert.Equal(frames * 960L, BitConverter.ToInt64(body, last + 6));
    }

    [Fact]
    public async Task Answers_range_requests_with_the_same_bytes()
    {
        var id = await Speak(Tone(3), Silence(2));
        var client = _server.CreateAuthorizedClient();
        var full = await client.GetByteArrayAsync($"/api/v1/conversations/{id}/audio");

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/conversations/{id}/audio");
        request.Headers.Range = new RangeHeaderValue(100, 199);
        var partial = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(100, 199, full.Length), partial.Content.Headers.ContentRange);
        Assert.Equal(full[100..200], await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Indexes_runs_by_capture_time()
    {
        var id = await Speak(Tone(3), Silence(2), Gap(60), Tone(2), Silence(2));

        var index = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}/audio/index");

        var runs = index.GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(0, runs[0].GetProperty("offsetMs").GetInt32());
        Assert.InRange(Ms(runs[0].GetProperty("startedAt")), StartMs, StartMs + 300);
        Assert.Equal(
            runs[1].GetProperty("offsetMs").GetInt32(),
            Ms(runs[0].GetProperty("endedAt")) - Ms(runs[0].GetProperty("startedAt")));
        Assert.Equal(
            index.GetProperty("durationMs").GetInt32() - runs[1].GetProperty("offsetMs").GetInt32(),
            Ms(runs[1].GetProperty("endedAt")) - Ms(runs[1].GetProperty("startedAt")));
    }

    [Fact]
    public async Task Index_and_stream_agree_on_the_duration()
    {
        var id = await Speak(Tone(3), Silence(2), Gap(60), Tone(2), Silence(2));
        var client = _server.CreateAuthorizedClient();

        var index = await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}/audio/index");
        var body = await client.GetByteArrayAsync($"/api/v1/conversations/{id}/audio");

        var last = body.AsSpan().LastIndexOf("OggS"u8);
        var granule = BitConverter.ToInt64(body, last + 6);
        Assert.Equal(index.GetProperty("durationMs").GetInt32(), granule / 48);
        var runs = index.GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal(2, runs.Count);
        Assert.True(runs[1].GetProperty("offsetMs").GetInt32() > 0);
        Assert.InRange(Ms(runs[1].GetProperty("startedAt")) - Ms(runs[0].GetProperty("startedAt")), 59_000, 70_000);
    }

    [Fact]
    public async Task Answers_404_without_speech_audio_and_for_unknown_conversations()
    {
        var id = await Speak(Tone(3), Silence(2));
        await db.ExecuteAsync("delete from speech_audio");
        var client = _server.CreateAuthorizedClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/conversations/{id}/audio")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/conversations/{id}/audio/index")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/conversations/{Guid.NewGuid()}/audio")).StatusCode);
    }

    [Fact]
    public async Task Read_tokens_may_play_and_no_token_may_not()
    {
        var id = await Speak(Tone(3), Silence(2));

        var reader = _server.CreateClientWithScope("read");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/conversations/{id}/audio")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/v1/conversations/{id}/audio/index")).StatusCode);

        var anonymous = _server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/conversations/{id}/audio")).StatusCode);
    }
}
