using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nytka.Server.Transcription;

namespace Nytka.Server.Tests.Transcription;

/// <summary>The direct Soniox client against a fake Soniox: the calls, the poll states, failures, and cleanup on every outcome.</summary>
public class SonioxClientTests
{
    private const string Key = "test-key";

    private readonly FakeSoniox _soniox = new();

    private readonly LogCapture _logs = new();

    private SonioxClient Client(Action<NytkaOptions.SttOptions>? configure = null, IReadOnlyList<string>? namedPeople = null, TimeSpan? maxWait = null, TimeSpan? callTimeout = null,
        TimeSpan? uploadTimeout = null)
    {
        var options = new NytkaOptions { Stt = { Url = "http://stt.test/v1/audio/transcriptions", SonioxApiKey = Key, SonioxBaseUrl = "https://soniox.test" } };
        configure?.Invoke(options.Stt);
        return new SonioxClient(
            new HttpClient(_soniox.CreateHandler()), Options.Create(options), _ => Task.FromResult(namedPeople ?? []), TimeProvider.System, _logs)
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            RetryDelay = TimeSpan.FromMilliseconds(1),
            CallTimeout = callTimeout ?? TimeSpan.FromSeconds(5),
            UploadTimeout = uploadTimeout ?? TimeSpan.FromSeconds(5),
            MaxWait = maxWait ?? TimeSpan.FromSeconds(10),
        };
    }

    private void AssertNothingLeft()
    {
        Assert.Equal(0, _soniox.StoredFiles);
        Assert.Equal(0, _soniox.StoredTranscriptions);
    }

    [Fact]
    public async Task Uploads_creates_polls_reads_and_deletes_both()
    {
        var wav = new byte[] { 1, 2, 3 };

        var result = await Client().TranscribeAsync(wav, default);

        Assert.Equal(
            [
                "POST /v1/files", "POST /v1/transcriptions", "GET /v1/transcriptions/tr-2", "GET /v1/transcriptions/tr-2/transcript",
                "DELETE /v1/transcriptions/tr-2", "DELETE /v1/files/file-1",
            ],
            _soniox.Calls);
        Assert.All(_soniox.Requests, r => Assert.Equal($"Bearer {Key}", r.Authorization));
        Assert.All(_soniox.Requests, r => Assert.Equal("soniox.test", r.Uri.Host));
        Assert.Equal(wav, _soniox.Requests[0].File);
        Assert.Equal("hello there", result.Text);
        AssertNothingLeft();
    }

    [Fact]
    public async Task Sends_the_model_language_hints_diarization_and_one_context()
    {
        await Client(s => s.Vocabulary = "Nytka, Opus", namedPeople: ["Anna", "nytka"]).TranscribeAsync([1], default);

        using var body = JsonDocument.Parse(_soniox.Requests[1].Body!);
        var root = body.RootElement;
        Assert.Equal("stt-async-v5", root.GetProperty("model").GetString());
        Assert.Equal("file-1", root.GetProperty("file_id").GetString());
        Assert.Equal(["uk", "ru", "en"], root.GetProperty("language_hints").EnumerateArray().Select(e => e.GetString()));
        Assert.True(root.GetProperty("enable_speaker_diarization").GetBoolean());
        var context = root.GetProperty("context");
        Assert.Equal(["Nytka", "Opus", "Anna"], context.GetProperty("terms").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("domain", Assert.Single(context.GetProperty("general").EnumerateArray()).GetProperty("key").GetString());
        Assert.False(context.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task A_set_language_is_the_only_hint()
    {
        await Client(s => s.Language = "uk").TranscribeAsync([1], default);

        using var body = JsonDocument.Parse(_soniox.Requests[1].Body!);
        Assert.Equal(["uk"], body.RootElement.GetProperty("language_hints").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task An_empty_base_url_is_api_soniox_com()
    {
        await Client(s => s.SonioxBaseUrl = "").TranscribeAsync([1], default);

        Assert.All(_soniox.Requests, r => Assert.Equal("api.soniox.com", r.Uri.Host));
    }

    [Fact]
    public async Task Polls_through_queued_and_processing_until_completed()
    {
        _soniox.Statuses = ["queued", "processing", "processing", "completed"];

        var result = await Client().TranscribeAsync([1], default);

        Assert.Equal(4, _soniox.Calls.Count(c => c == "GET /v1/transcriptions/tr-2"));
        Assert.Equal("hello there", result.Text);
        AssertNothingLeft();
    }

    [Fact]
    public async Task A_transcription_error_fails_without_its_message_and_deletes_both()
    {
        _soniox.Statuses = ["processing", "error"];

        var error = await Assert.ThrowsAsync<TranscriptionException>(() => Client().TranscribeAsync([1], default));

        Assert.Equal("Soniox could not transcribe the audio.", error.Message);
        AssertNothingLeft();
    }

    [Fact]
    public async Task A_slow_answer_gives_up_and_deletes_both()
    {
        _soniox.Statuses = ["processing"];

        var error = await Assert.ThrowsAsync<TranscriptionException>(
            () => Client(maxWait: TimeSpan.FromMilliseconds(50)).TranscribeAsync([1], default));

        Assert.StartsWith("Soniox did not finish within", error.Message);
        AssertNothingLeft();
    }

    [Fact]
    public async Task A_cancelled_request_still_deletes_both()
    {
        _soniox.Statuses = ["processing"];
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client().TranscribeAsync([1], cancel.Token));

        Assert.Contains("DELETE /v1/transcriptions/tr-2", _soniox.Calls);
        AssertNothingLeft();
    }

    [Theory]
    [InlineData("POST /v1/files")]
    [InlineData("POST /v1/transcriptions")]
    [InlineData("GET /v1/transcriptions/tr-2")]
    [InlineData("GET /v1/transcriptions/tr-2/transcript")]
    public async Task A_lasting_429_anywhere_fails_with_the_status_only_and_leaves_nothing(string call)
    {
        _soniox.Fail = c => c == call ? HttpStatusCode.TooManyRequests : null;

        var error = await Assert.ThrowsAsync<TranscriptionException>(() => Client().TranscribeAsync([1], default));

        Assert.Equal("Soniox answered 429.", error.Message);
        AssertNothingLeft();
    }

    [Theory]
    [InlineData("GET /v1/transcriptions/tr-2", HttpStatusCode.TooManyRequests)]
    [InlineData("GET /v1/transcriptions/tr-2", HttpStatusCode.BadGateway)]
    [InlineData("GET /v1/transcriptions/tr-2/transcript", HttpStatusCode.TooManyRequests)]
    public async Task A_transient_failure_on_a_transcription_in_flight_is_tried_again(string call, HttpStatusCode status)
    {
        _soniox.Statuses = ["processing", "completed"];
        var failures = 0;
        _soniox.Fail = c => c == call && failures++ < 3 ? status : null;

        var result = await Client().TranscribeAsync([1], default);

        Assert.Equal("hello there", result.Text);
        Assert.Equal(1, _soniox.Calls.Count(c => c == "POST /v1/transcriptions"));
        AssertNothingLeft();
    }

    [Fact]
    public async Task A_poll_that_times_out_once_is_tried_again()
    {
        var slow = 1;
        _soniox.Delay = c => c == "GET /v1/transcriptions/tr-2" && slow-- > 0 ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;

        var result = await Client(callTimeout: TimeSpan.FromMilliseconds(100)).TranscribeAsync([1], default);

        Assert.Equal("hello there", result.Text);
        AssertNothingLeft();
    }

    [Fact]
    public async Task A_transient_failure_that_persists_fails_and_deletes_both()
    {
        _soniox.Fail = c => c == "GET /v1/transcriptions/tr-2" ? HttpStatusCode.ServiceUnavailable : null;

        var error = await Assert.ThrowsAsync<TranscriptionException>(() => Client().TranscribeAsync([1], default));

        Assert.Equal("Soniox answered 503.", error.Message);
        Assert.Equal(4, _soniox.Calls.Count(c => c == "GET /v1/transcriptions/tr-2"));
        AssertNothingLeft();
    }

    [Fact]
    public async Task A_bad_request_on_a_poll_is_not_tried_again()
    {
        _soniox.Fail = c => c == "GET /v1/transcriptions/tr-2" ? HttpStatusCode.BadRequest : null;

        await Assert.ThrowsAsync<TranscriptionException>(() => Client().TranscribeAsync([1], default));

        Assert.Equal(1, _soniox.Calls.Count(c => c == "GET /v1/transcriptions/tr-2"));
        AssertNothingLeft();
    }

    [Fact]
    public async Task The_upload_has_its_own_timeout_and_a_timeout_is_a_transcription_failure()
    {
        _soniox.Delay = c => c == "POST /v1/files" ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero;

        // Longer than the other calls may take: the upload still goes through.
        var result = await Client(callTimeout: TimeSpan.FromMilliseconds(100), uploadTimeout: TimeSpan.FromSeconds(5)).TranscribeAsync([1], default);
        Assert.Equal("hello there", result.Text);

        var error = await Assert.ThrowsAsync<TranscriptionException>(
            () => Client(uploadTimeout: TimeSpan.FromMilliseconds(100)).TranscribeAsync([1], default));
        Assert.Equal("Soniox did not answer in time or could not be reached.", error.Message);
        AssertNothingLeft();
    }

    [Fact]
    public async Task Polls_further_apart_as_the_wait_grows()
    {
        _soniox.Statuses = ["processing"];
        var client = new SonioxClient(
            new HttpClient(_soniox.CreateHandler()),
            Options.Create(new NytkaOptions { Stt = { SonioxApiKey = Key, SonioxBaseUrl = "https://soniox.test" } }),
            _ => Task.FromResult<IReadOnlyList<string>>([]),
            TimeProvider.System,
            _logs)
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            MaxPollInterval = TimeSpan.FromMilliseconds(80),
            MaxWait = TimeSpan.FromMilliseconds(600),
        };

        await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync([1], default));

        // At a fixed 20 ms about 30 polls fit in 600 ms; growing to 80 ms, about 10.
        Assert.InRange(_soniox.Calls.Count(c => c == "GET /v1/transcriptions/tr-2"), 5, 16);
        AssertNothingLeft();
    }

    [Fact]
    public async Task A_failed_delete_is_logged_without_the_key_and_does_not_fail_the_request()
    {
        _soniox.Fail = c => c.StartsWith("DELETE /v1/files", StringComparison.Ordinal) ? HttpStatusCode.InternalServerError : null;

        var result = await Client().TranscribeAsync([1], default);

        Assert.Equal("hello there", result.Text);
        var line = Assert.Single(_logs.Lines);
        Assert.Contains("500", line);
        Assert.DoesNotContain(Key, line);
        Assert.DoesNotContain("secret words", line);
    }

    [Fact]
    public async Task Logs_nothing_on_success_and_no_key_or_text_on_failure()
    {
        await Client().TranscribeAsync([1], default);
        _soniox.Statuses = ["error"];
        await Assert.ThrowsAsync<TranscriptionException>(() => Client().TranscribeAsync([1], default));

        Assert.Empty(_logs.Lines);
    }

    [Fact]
    public void Splits_tokens_at_speaker_changes_and_one_second_pauses()
    {
        var result = SonioxClient.Parse(
            """
            {"text":"Привіт як справи? Добре. Ну",
             "tokens":[
              {"text":"При","start_ms":100,"end_ms":300,"speaker":"1"},
              {"text":"віт","start_ms":300,"end_ms":500,"speaker":"1"},
              {"text":" як","start_ms":600,"end_ms":800,"speaker":"1"},
              {"text":" справи?","start_ms":900,"end_ms":1400,"speaker":"1"},
              {"text":" Добре.","start_ms":1500,"end_ms":2000,"speaker":"2"},
              {"text":" Ну","start_ms":3000,"end_ms":3200,"speaker":"2"},
              {"text":"<music>","start_ms":3200,"end_ms":4000,"is_audio_event":true},
              {"text":" untimed"}]}
            """);

        Assert.Equal(
            [
                new TranscribedSegment(0.1, 1.4, "Привіт як справи?", "SPEAKER_1"),
                new TranscribedSegment(1.5, 2.0, "Добре.", "SPEAKER_2"),
                new TranscribedSegment(3.0, 3.2, "Ну", "SPEAKER_2"),
            ],
            result.Segments);
        Assert.Equal("Привіт як справи? Добре. Ну", result.Text);
    }

    [Fact]
    public void A_transcript_without_tokens_has_its_text_and_no_segments()
    {
        var result = SonioxClient.Parse("""{"text":" hello "}""");

        Assert.Equal("hello", result.Text);
        Assert.Empty(result.Segments);
    }

    [Fact]
    public void Invalid_json_fails_with_a_fixed_message()
    {
        Assert.Equal("Soniox answered with invalid JSON.", Assert.Throws<TranscriptionException>(() => SonioxClient.Parse("secret words")).Message);
    }

    private sealed class LogCapture : ILogger<SonioxClient>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }
}
