using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.DayEvents;
using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Tests.DayEvents;

/// <summary>
/// Day events (docs/explanation/features.md#day-events): a fixture day's exact list, recompute idempotency and
/// sensitivity-class hiding.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DayEventTests(PostgresFixture db) : Ai.AiTestBase(db)
{
    private static readonly Guid ConversationA = Guid.Parse("018f0000-0000-7000-8000-00000000000a");
    private static readonly Guid ConversationB = Guid.Parse("018f0000-0000-7000-8000-00000000000b");
    private static readonly Guid Olena = Guid.Parse("018f0000-0000-7000-8000-0000000000f1");
    private static readonly DateTime Day = new(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);

    private HttpClient Admin => Server.CreateAuthorizedClient();

    /// <summary>
    /// The fixture day: a conversation where only the wearer speaks (<c>alone</c>), a 30-minute gap to the next one
    /// (<c>silence</c>), a named person who joins a second conversation late (<c>person_joined</c>/<c>person_left</c>),
    /// and a now-playing range later in the day (<c>media_started</c>/<c>media_stopped</c>).
    /// </summary>
    private async Task SeedDayAsync(string? sensitivityB = null)
    {
        await Db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, sensitivity, created_at, updated_at)
            values (@a, @aStart, @aEnd, 'closed', null, @aStart, @aStart),
                   (@b, @bStart, @bEnd, 'closed', @sensitivityB, @bStart, @bStart);
            insert into transcription_batches (id, conversation_id, started_at, ended_at, status, offset_map, created_at)
            overriding system value
            values (101, @a, @aStart, @aEnd, 'done', '[]', @aStart), (102, @b, @bStart, @bEnd, 'done', '[]', @bStart);
            insert into people (id, name, created_at) values (@olena, 'Olena', @aStart);
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, is_user)
            values (@a, 101, @aStart, @aStart + interval '2 minutes', 'Just me today.', true);
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, is_user)
            values (@b, 102, @bStart, @bStart + interval '5 seconds', 'Hello there.', true);
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, person_id)
            values (@b, 102, @bStart + interval '5 minutes', @bStart + interval '5 minutes 5 seconds', 'Hi, sorry I am late.', @olena),
                   (@b, 102, @bStart + interval '9 minutes', @bStart + interval '9 minutes 10 seconds', 'No worries at all.', @olena);
            insert into context_ranges (id, kind, route, started_at, ended_at, detail, received_at)
            values (gen_random_uuid(), 'now-playing', 'speaker', @mediaStart, @mediaEnd, 'video', @mediaStart);
            """,
            new
            {
                a = ConversationA, b = ConversationB, olena = Olena, sensitivityB,
                aStart = Day.AddHours(9), aEnd = Day.AddHours(9).AddMinutes(2),
                bStart = Day.AddHours(9).AddMinutes(32), bEnd = Day.AddHours(9).AddMinutes(42),
                mediaStart = Day.AddHours(10), mediaEnd = Day.AddHours(10).AddMinutes(10),
            });
    }

    private async Task RecomputeAsync()
    {
        await Server.Get<JobQueue>().EnqueueAsync(
            JobKinds.RecomputeEvents, new DayEventsPayload("2026-09-29"), JobKinds.RecomputeEventsKey("2026-09-29"), Now, default);
        await Server.RunJobsAsync();
    }

    private Task<List<EventRow>> StoredEventsAsync() => Db.QueryAsync<EventRow>(
        "select kind as Kind, at as At, conversation_id as ConversationId, person_id as PersonId, voice_label as VoiceLabel, "
        + "detail::text as Detail, source as Source from events order by at, kind");

    private sealed record EventRow(string Kind, DateTime At, Guid? ConversationId, Guid? PersonId, string? VoiceLabel, string Detail, string Source);

    [Fact]
    public async Task A_fixture_day_gives_the_exact_event_list()
    {
        await SeedDayAsync();

        await RecomputeAsync();

        var rows = await StoredEventsAsync();
        Assert.Equal(
            [
                (DayEventKinds.Alone, Day.AddHours(9), ConversationA, (Guid?)null, (string?)null, "segments"),
                (DayEventKinds.Silence, Day.AddHours(9).AddMinutes(2), (Guid?)null, (Guid?)null, (string?)null, "segments"),
                (DayEventKinds.PersonJoined, Day.AddHours(9).AddMinutes(37), ConversationB, Olena, "Olena", "segments"),
                (DayEventKinds.PersonLeft, Day.AddHours(9).AddMinutes(41).AddSeconds(10), ConversationB, Olena, "Olena", "segments"),
                (DayEventKinds.MediaStarted, Day.AddHours(10), (Guid?)null, (Guid?)null, (string?)null, "context"),
                (DayEventKinds.MediaStopped, Day.AddHours(10).AddMinutes(10), (Guid?)null, (Guid?)null, (string?)null, "context"),
            ],
            rows.Select(r => (r.Kind, r.At, r.ConversationId, r.PersonId, r.VoiceLabel, r.Source)));
        Assert.Equal("""{"minutes":30}""", rows.Single(r => r.Kind == DayEventKinds.Silence).Detail);
        Assert.Equal("""{"category":"video"}""", rows.Single(r => r.Kind == DayEventKinds.MediaStarted).Detail);

        var response = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/events?date=2026-09-29");
        Assert.Equal("2026-09-29", response.GetProperty("date").GetString());
        var events = response.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(6, events.Count);
        Assert.Equal("Olena", events.Single(e => e.GetProperty("kind").GetString() == DayEventKinds.PersonJoined).GetProperty("voiceLabel").GetString());
    }

    [Fact]
    public async Task Recomputing_twice_gives_the_same_rows()
    {
        await SeedDayAsync();
        await RecomputeAsync();
        var first = await StoredEventsAsync();

        await RecomputeAsync();
        var second = await StoredEventsAsync();

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(
            first.Select(r => (r.Kind, r.At, r.ConversationId, r.PersonId, r.VoiceLabel, r.Detail, r.Source)).OrderBy(r => r.At).ThenBy(r => r.Kind),
            second.Select(r => (r.Kind, r.At, r.ConversationId, r.PersonId, r.VoiceLabel, r.Detail, r.Source)).OrderBy(r => r.At).ThenBy(r => r.Kind));
    }

    [Fact]
    public async Task An_event_tied_to_a_hidden_conversation_is_absent_for_read_and_present_for_admin()
    {
        await SeedDayAsync(sensitivityB: "health");
        await RecomputeAsync();

        var admin = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/events?date=2026-09-29");
        var read = await Server.CreateClientWithScope("read").GetFromJsonAsync<JsonElement>("/api/v1/events?date=2026-09-29");

        Assert.Contains(admin.GetProperty("events").EnumerateArray(), e => e.GetProperty("kind").GetString() == DayEventKinds.PersonJoined);
        Assert.DoesNotContain(read.GetProperty("events").EnumerateArray(), e => e.GetProperty("kind").GetString() == DayEventKinds.PersonJoined);
        // The first conversation (alone) and the context-only media events carry no conversation, so they stay visible either way.
        Assert.Contains(read.GetProperty("events").EnumerateArray(), e => e.GetProperty("kind").GetString() == DayEventKinds.Alone);
        Assert.Equal(HttpStatusCode.OK, (await Server.CreateClientWithScope("read").GetAsync("/api/v1/events?date=2026-09-29")).StatusCode);
    }

    [Fact]
    public async Task A_bad_zone_or_date_is_a_400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/events?tz=Mars/Base")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/events?date=yesterday")).StatusCode);
    }
}
