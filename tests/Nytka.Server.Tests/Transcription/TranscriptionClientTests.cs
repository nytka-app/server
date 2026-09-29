using System.Net;
using Microsoft.Extensions.Options;
using Nytka.Server.Transcription;

namespace Nytka.Server.Tests.Transcription;

public class TranscriptionClientTests
{
    private readonly FakeStt _stt = new();

    private TranscriptionClient Client(Action<NytkaOptions.SttOptions>? configure = null)
    {
        var options = new NytkaOptions { Stt = { Url = "http://stt.test/v1/audio/transcriptions" } };
        configure?.Invoke(options.Stt);
        return new TranscriptionClient(new HttpClient(_stt.CreateHandler()), Options.Create(options));
    }

    [Fact]
    public async Task Sends_the_wav_and_every_setting()
    {
        var wav = new byte[] { 1, 2, 3 };

        await Client(s => { s.ApiKey = "key-1"; s.Model = "whisper-1"; s.Language = "uk"; }).TranscribeAsync(wav, default);

        var request = Assert.Single(_stt.Requests);
        Assert.Equal("http://stt.test/v1/audio/transcriptions", request.Uri?.ToString());
        Assert.Equal("Bearer key-1", request.Authorization);
        Assert.Equal(wav, request.File);
        Assert.Equal("audio.wav", request.FileName);
        Assert.Equal("audio/wav", request.FileType);
        Assert.Equal("verbose_json", request.Fields["response_format"]);
        Assert.Equal("whisper-1", request.Fields["model"]);
        Assert.Equal("uk", request.Fields["language"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    public async Task Leaves_out_settings_that_are_not_set(string? language)
    {
        await Client(s => s.Language = language).TranscribeAsync([1], default);

        var request = Assert.Single(_stt.Requests);
        Assert.Null(request.Authorization);
        Assert.Equal(["response_format"], request.Fields.Keys);
    }

    [Fact]
    public async Task Parses_text_and_segments()
    {
        var result = await Client().TranscribeAsync([1], default);

        Assert.Equal("hello there", result.Text);
        Assert.Equal(
            [new TranscribedSegment(0.0, 1.0, "hello"), new TranscribedSegment(1.0, 2.0, "there")],
            result.Segments);
        Assert.Equal(FakeStt.DefaultJson, result.RawJson);
    }

    [Theory]
    [InlineData("""{"text":"hi"}""", 0)]
    [InlineData("""{"text":"hi","segments":null}""", 0)]
    [InlineData("""{"text":"hi","segments":[{"start":"x"},{"start":1,"end":2,"text":"ok"}],"extra":1}""", 1)]
    [InlineData("""{"segments":[{"start":1,"end":0.5,"text":"backwards"}]}""", 1)]
    public void Parses_leniently(string json, int segments)
    {
        var result = TranscriptionClient.Parse(json);

        Assert.Equal(segments, result.Segments.Count);
        Assert.All(result.Segments, s => Assert.True(s.End >= s.Start));
    }

    [Fact]
    public async Task Failure_reports_the_status_code_and_never_the_body()
    {
        _stt.Respond = _ => FakeStt.Json("""{"error":"secret words from the audio"}""", HttpStatusCode.ServiceUnavailable);

        var error = await Assert.ThrowsAsync<TranscriptionException>(() => Client().TranscribeAsync([1], default));

        Assert.Contains("503", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void Rejects_an_answer_that_is_not_a_json_object(string raw) =>
        Assert.Throws<TranscriptionException>(() => TranscriptionClient.Parse(raw));
}
