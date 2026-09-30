using Nytka.Server.Jobs;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Pipeline;

/// <summary>
/// The pendant records while the phone is away and the phone mutes only live audio, so the server
/// drops audio captured inside a mute window. The audio starts at 2026-09-29 10:00 UTC, a Tuesday,
/// which is 13:00 in Europe/Kyiv; the window is 13:01 to 13:02 local, so 60 to 120 seconds in.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MuteWindowTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Window = """[{"days":[2],"start":"13:01","end":"13:02"}]""";

    private readonly Guid _session = Guid.NewGuid();
    private readonly List<NytkaApiFactory> _servers = [];

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _servers.ForEach(s => s.Dispose());
        return Task.CompletedTask;
    }

    private NytkaApiFactory Server(string? windows, string? zone = "Europe/Kyiv")
    {
        var server = new NytkaApiFactory(db, settings =>
        {
            if (windows is not null)
            {
                settings["Nytka:Mute:Windows"] = windows;
            }

            if (zone is not null)
            {
                settings["Nytka:User:TimeZone"] = zone;
            }
        });
        _servers.Add(server);
        return server;
    }

    private Task<long> Count(string table, string where = "true") =>
        db.ScalarAsync<long>($"select count(*) from {table} where {where}");

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private async Task<List<(long Start, long End)>> Batches() =>
        (await db.QueryAsync<(DateTime, DateTime)>("select started_at, ended_at from transcription_batches order by started_at"))
        .Select(b => (Ms(b.Item1), Ms(b.Item2))).ToList();

    private async Task Process(NytkaApiFactory server, params Part[] parts)
    {
        await Process(server, _session, parts);
    }

    /// <summary>Uploads, runs, then lets the session go idle so speech still pending closes.</summary>
    private static async Task Process(NytkaApiFactory server, Guid session, Part[] parts)
    {
        await server.UploadAsync(Chunks(session, parts));
        await server.RunJobsAsync();
        server.Time.Advance(TimeSpan.FromSeconds(61));
        await server.Get<Scheduler>().TickAsync(default);
        await server.RunJobsAsync();
    }

    [Fact]
    public async Task Speech_inside_a_window_produces_nothing()
    {
        var server = Server(Window);

        await Process(server, Silence(65), Tone(10), Silence(3));

        Assert.Equal(0, await Count("transcription_batches"));
        Assert.Equal(0, await Count("speech_audio"));
        Assert.Equal(0, await Count("segments"));
        Assert.Equal(0, await Count("conversations"));
        Assert.Empty(server.Stt.Requests);
        Assert.Equal(0, await Count("audio_chunks", "processed_at is null"));
    }

    [Fact]
    public async Task Speech_running_into_a_window_is_cut_at_its_start()
    {
        var server = Server(Window);

        await Process(server, Silence(50), Tone(20), Silence(3));

        var (start, end) = Assert.Single(await Batches());
        Assert.InRange(start, StartMs + 49_700, StartMs + 49_900);
        Assert.InRange(end, StartMs + 59_900, StartMs + 60_000);
        var audio = Assert.Single(await db.QueryAsync<(DateTime, DateTime)>("select started_at, ended_at from speech_audio"));
        Assert.InRange(Ms(audio.Item2), StartMs + 59_900, StartMs + 60_000);
    }

    [Fact]
    public async Task Speech_leaving_a_window_starts_at_its_end()
    {
        var server = Server(Window);

        await Process(server, Silence(110), Tone(15), Silence(3));

        var (start, end) = Assert.Single(await Batches());
        Assert.InRange(start, StartMs + 120_000, StartMs + 120_100);
        Assert.InRange(end, StartMs + 125_000, StartMs + 125_300);
        var audio = Assert.Single(await db.QueryAsync<(DateTime, DateTime)>("select started_at, ended_at from speech_audio"));
        Assert.InRange(Ms(audio.Item1), StartMs + 120_000, StartMs + 120_100);
    }

    [Fact]
    public async Task Speech_across_a_whole_window_keeps_both_sides_and_nothing_between()
    {
        var server = Server(Window);

        await Process(server, Silence(58), Tone(65), Silence(3));

        var batches = await Batches();
        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.True(b.End <= StartMs + 60_000 || b.Start >= StartMs + 120_000));
        var pieces = await db.QueryAsync<(DateTime, DateTime)>("select started_at, ended_at from speech_audio");
        Assert.All(pieces, p => Assert.True(Ms(p.Item2) <= StartMs + 60_000 || Ms(p.Item1) >= StartMs + 120_000));
    }

    [Fact]
    public async Task A_window_on_another_weekday_drops_nothing()
    {
        var server = Server("""[{"days":[3],"start":"13:01","end":"13:02"}]""");

        await Process(server, Silence(65), Tone(10), Silence(3));

        Assert.Equal(1, await Count("transcription_batches"));
    }

    [Fact]
    public async Task Windows_follow_the_configured_time_zone()
    {
        var utc = Server(Window, zone: null);
        await Process(utc, Silence(65), Tone(10), Silence(3));

        Assert.Equal(1, await Count("transcription_batches")); // 13:01 UTC is not 10:01 UTC
    }

    [Fact]
    public async Task With_no_windows_speech_is_processed_as_before()
    {
        var server = Server(null);

        await Process(server, Silence(65), Tone(10), Silence(3));

        Assert.Equal(1, await Count("transcription_batches"));
    }

    [Fact]
    public async Task Stored_audio_uploaded_late_is_dropped_by_capture_time()
    {
        var server = Server(Window);
        await Process(server, Tone(6), Silence(3));            // live speech before the window
        server.Time.Advance(TimeSpan.FromHours(3));            // uploaded long after it was captured

        var stored = Guid.NewGuid();
        await Process(server, stored, [Gap(70), Tone(8), Silence(3)]);

        Assert.Equal(1, await Count("transcription_batches"));
        Assert.Equal(1, await Count("speech_audio"));
    }
}
