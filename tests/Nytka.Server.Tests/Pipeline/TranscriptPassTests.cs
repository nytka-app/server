using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Server.Api;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Tests.Ai;
using Nytka.Server.Tests.Webhooks;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Pipeline;

/// <summary>The paid pass and its atomic swap (docs/specs/auto-transcription.md), against the fake Soniox.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TranscriptPassTests(PostgresFixture db) : AiTestBase(db)
{
    /// <summary>Tokens in seconds into the pass WAV: "first" over the draft in its first stretch, "second" 200 ms into its second (3.3 s + 0.2 s).</summary>
    private const string PaidTranscript =
        """
        {"text":"first second","tokens":[
          {"text":"first","start_ms":0,"end_ms":1000,"speaker":"1"},
          {"text":"second","start_ms":3500,"end_ms":4100,"speaker":"1"}]}
        """;

    private HttpClient Client => Server.CreateAuthorizedClient();

    private Task<long> Count(string table, string where = "true") => Db.ScalarAsync<long>($"select count(*) from {table} where {where}");

    private async Task<T> Row<T>(string sql, object? args = null) => (await Db.QueryAsync<T>(sql, args)).Single();

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>
    /// A conversation of two stretches 2 s apart in capture time, drafted by Soniox (one line, "hello there"), marked by the owner and
    /// named by a rejected suggestion, a fact and a voice match. Returns the conversation and the draft line.
    /// </summary>
    private async Task<(Guid Conversation, long Draft, Guid Person)> DraftedAsync(Action<IDictionary<string, string?>>? configure = null)
    {
        StartServer(s =>
        {
            s["Nytka:Stt:SonioxApiKey"] = "test-key";
            configure?.Invoke(s);
        });
        await Db.ResetAsync();
        await Server.UploadAsync(Chunks(Guid.NewGuid(), Tone(3), Gap(2), Tone(3), Silence(3)));
        await Server.RunJobsAsync();

        var conversation = await Db.ScalarAsync<Guid>("select id from conversations");
        var draft = await Db.ScalarAsync<long>("select id from segments");
        var person = Guid.NewGuid();
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@person, 'Anna', now())", new { person });
        await Db.ExecuteAsync(
            "update segments set speaker_id = '7', is_user_manual = true, speech_manual = 'person', person_id = @person where id = @draft",
            new { person, draft });
        await Db.ExecuteAsync(
            """
            insert into name_suggestions (id, conversation_id, target, segment_ids, name, evidence_segment_id, confidence, status, created_at)
            values (@id, @conversation, 'label', array[@draft]::bigint[], 'Anna', @draft, 0.9, 'rejected', now())
            """,
            new { id = Guid.NewGuid(), conversation, draft });
        await Db.ExecuteAsync(
            """
            insert into person_facts (id, person_id, text, fingerprint, source, basis, conversation_id, segment_id, created_at, updated_at)
            values (@id, @person, 'Likes tea', 'likes-tea', 'ai', 'said', @conversation, @draft, now(), now())
            """,
            new { id = Guid.NewGuid(), person, conversation, draft });
        await Db.ExecuteAsync(
            """
            insert into voice_matches (id, conversation_id, person_id, segment_ids, similarity, created_at)
            values (@id, @conversation, @person, array[@draft]::bigint[], 0.9, now())
            """,
            new { id = Guid.NewGuid(), conversation, person, draft });
        Server.Soniox.Transcript = PaidTranscript;
        return (conversation, draft, person);
    }

    private async Task UpgradeAsync(Guid conversation, HttpStatusCode expected = HttpStatusCode.Accepted)
    {
        Assert.Equal(expected, (await Client.PostAsync($"/api/v1/conversations/{conversation}/upgrade", null)).StatusCode);
    }

    [Fact]
    public async Task A_pass_swaps_the_draft_for_the_paid_lines_at_their_true_capture_times()
    {
        var (conversation, draft, person) = await DraftedAsync();
        Server.Soniox.Statuses = ["processing", "completed"];

        await UpgradeAsync(conversation);
        await Server.RunJobsAsync();

        // Submitted, not swapped: the lane was handed back after the upload.
        Assert.Equal(1, await Count("transcript_passes", "status = 'submitted' and external_id is not null"));
        Assert.Equal(["hello there"], await Db.QueryAsync<string>("select text from segments"));

        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();

        var lines = await Db.QueryAsync<(long Id, string Text, DateTime Start, long? Pass, string? SpeakerId, bool? Manual, Guid? Person)>(
            "select id, text, started_at, pass_id, speaker_id, is_user_manual, person_id from segments order by started_at");
        Assert.Equal(["first", "second"], lines.Select(l => l.Text));
        // At the start of the first stretch; 3.5 s into the WAV is 200 ms into the second, which starts 5 s after the first in capture time.
        Assert.Equal([StartMs, StartMs + 5_000 + 200], lines.Select(l => Ms(l.Start)));
        Assert.All(lines, l => Assert.NotNull(l.Pass));
        // "first" overlaps the draft the owner marked; "second" overlaps nothing but shares Soniox's speaker 1 with it.
        Assert.Equal((true, person), (lines[0].Manual, lines[0].Person));
        Assert.Equal((null, null), (lines[1].Manual, lines[1].Person));
        Assert.Equal(["7", "7"], lines.Select(l => l.SpeakerId));

        Assert.Equal(["hello there"], await Db.QueryAsync<string>("select text from segment_drafts"));
        Assert.Equal(draft, await Db.ScalarAsync<long>("select id from segment_drafts"));
        var first = lines[0].Id;
        Assert.Equal(("rejected", first), await Row<(string, long)>("select status, evidence_segment_id from name_suggestions"));
        Assert.Equal([first], await Db.ScalarAsync<long[]>("select segment_ids from name_suggestions"));
        Assert.Equal(first, await Db.ScalarAsync<long>("select segment_id from person_facts"));
        Assert.Equal([first], await Db.ScalarAsync<long[]>("select segment_ids from voice_matches"));

        Assert.Equal(("final", "done"), await Row<(string, string)>(
            "select c.transcript_tier, p.status from conversations c join transcript_passes p on p.conversation_id = c.id"));
        Assert.Equal(1, await Count("transcription_batches", "pass_id is not null"));
        var row = await Row<(long?, string, long, int)>("select batch_id, engine, audio_ms, text_chars from transcription_usage where pass_id is not null");
        Assert.Equal((null, "soniox", "first second".Length), (row.Item1, row.Item2, row.Item4));
        // The two stretches and the 300 ms between them, plus the detector's padding.
        Assert.Equal(await Db.ScalarAsync<int>("select audio_ms from transcript_passes"), row.Item3);
        Assert.InRange(row.Item3, 6_300, 7_000);

        // One file, one transcription for the batch's draft, one pair for the pass; nothing left at Soniox.
        Assert.Equal(2, Server.Soniox.Calls.Count(c => c == "POST /v1/transcriptions"));
        Assert.Equal(0, Server.Soniox.StoredFiles);
        Assert.Equal(0, Server.Soniox.StoredTranscriptions);
        Assert.Equal(1, Events.Types.Count(t => t == "transcript.upgraded"));
        Assert.Equal(0, await Count("jobs", "kind = 'transcribe-conversation'"));
    }

    [Fact]
    public async Task A_failure_inside_the_swap_leaves_the_whole_draft()
    {
        var (conversation, draft, _) = await DraftedAsync();
        await UpgradeAsync(conversation);
        Events.ThrowOn = "transcript.upgraded";

        await Server.RunJobsAsync();

        // The subscriber threw inside the swap's transaction: nothing of the swap remains, and the paid answer is kept for the retry.
        Assert.Equal([draft], await Db.QueryAsync<long>("select id from segments"));
        Assert.Equal(0, await Count("segment_drafts"));
        Assert.Equal(0, await Count("transcription_usage", "pass_id is not null"));
        Assert.Equal(0, await Count("transcription_batches", "pass_id is not null"));
        Assert.Equal("draft", await Db.ScalarAsync<string>("select transcript_tier from conversations"));
        Assert.Equal(draft, await Db.ScalarAsync<long>("select evidence_segment_id from name_suggestions"));
        Assert.Equal(1, await Count("transcript_passes", "status = 'submitted' and external_id is not null"));

        Events.ThrowOn = null;
        Server.Time.Advance(TimeSpan.FromSeconds(30));
        await Server.RunJobsAsync();

        Assert.Equal(["first", "second"], await Db.QueryAsync<string>("select text from segments order by started_at"));
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from transcript_passes"));
        Assert.Equal(0, Server.Soniox.StoredFiles);
    }

    [Fact]
    public async Task A_pass_whose_batch_moved_is_stale_and_swaps_nothing_but_the_request_is_in_the_ledger()
    {
        var (conversation, draft, _) = await DraftedAsync();
        Server.Soniox.Statuses = ["processing", "completed"];
        await UpgradeAsync(conversation);
        await Server.RunJobsAsync();
        await Db.ExecuteAsync("update transcription_batches set status = 'failed'");

        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();

        Assert.Equal("stale", await Db.ScalarAsync<string>("select status from transcript_passes"));
        Assert.Equal([draft], await Db.QueryAsync<long>("select id from segments"));
        Assert.Equal(1, await Count("transcription_usage", "pass_id is not null"));
        Assert.Equal(0, Server.Soniox.StoredFiles);
        Assert.Equal(0, Server.Soniox.StoredTranscriptions);
    }

    [Fact]
    public async Task A_failed_pass_keeps_the_draft_cleans_up_and_can_be_tried_again()
    {
        var (conversation, draft, _) = await DraftedAsync();
        Server.Soniox.Statuses = ["error"];
        await UpgradeAsync(conversation);

        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(30));
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromMinutes(2));
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromMinutes(2));
        await Server.RunJobsAsync();

        var (status, error) = await Row<(string, string)>("select status, error from transcript_passes");
        Assert.Equal(("failed", "Soniox could not transcribe the audio."), (status, error));
        Assert.Equal([draft], await Db.QueryAsync<long>("select id from segments"));
        Assert.Equal(0, Server.Soniox.StoredFiles);
        Assert.Equal(0, Server.Soniox.StoredTranscriptions);
        Assert.Equal(0, await Count("transcription_usage", "pass_id is not null"));

        Server.Soniox.Statuses = ["completed"];
        await UpgradeAsync(conversation);
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();

        Assert.Equal(["failed", "done"], await Db.QueryAsync<string>("select status from transcript_passes order by id"));
        Assert.Equal(["first", "second"], await Db.QueryAsync<string>("select text from segments order by started_at"));
    }

    [Fact]
    public async Task A_swap_runs_the_summary_and_the_memories_again_and_tells_the_webhook_once()
    {
        await using var receiver = await TestReceiver.StartAsync();
        var (conversation, _, _) = await DraftedAsync();
        (await Client.PostAsJsonAsync("/api/v1/webhooks", new { url = receiver.Url, events = new[] { "transcript.upgraded" } })).EnsureSuccessStatusCode();
        await Db.ExecuteAsync("update conversations set status = 'closed'");
        await Db.ExecuteAsync(
            "insert into memory_runs (conversation_id, status, through_segment_id, failures, updated_at) values (@conversation, 'done', 99, 0, now())",
            new { conversation });
        await Db.ExecuteAsync("update conversations set ai_status = 'done', ai_through_segment_id = 1 where id = @conversation", new { conversation });

        await UpgradeAsync(conversation);
        Llm.Respond = _ => FakeLlm.Answer("Paid title", "Paid summary");
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();

        // The summary ran again on the final text (two words: too short, so skipped) and read up to the newest paid line.
        Assert.Equal(("skipped", await MaxSegmentId(conversation)), await Row<(string, long?)>("select ai_status, ai_through_segment_id from conversations") is var (ai, through) ? (ai, through!.Value) : default);
        Assert.Equal(("pending", (long?)null), await Row<(string, long?)>("select status, through_segment_id from memory_runs"));

        var delivered = receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement).Where(b => b.GetProperty("type").GetString() == "transcript.upgraded").ToList();
        var data = Assert.Single(delivered).GetProperty("data");
        Assert.Equal(conversation, data.GetProperty("conversationId").GetGuid());
        Assert.Equal(("soniox", 1, 2, "final"), (data.GetProperty("engine").GetString(), data.GetProperty("replaced").GetInt32(), data.GetProperty("inserted").GetInt32(), data.GetProperty("tier").GetString()));
        Assert.DoesNotContain("first", System.Text.Encoding.UTF8.GetString(receiver.Requests.First().Body));
    }

    [Fact]
    public async Task The_route_queues_one_pass_and_refuses_what_cannot_be_paid()
    {
        var (conversation, _, _) = await DraftedAsync();

        await UpgradeAsync(conversation);
        await UpgradeAsync(conversation);
        Assert.Equal(1, await Count("jobs", "kind = 'transcribe-conversation'"));
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();
        await UpgradeAsync(conversation, HttpStatusCode.OK);

        var detail = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversation}");
        var transcript = detail.GetProperty("transcript");
        Assert.Equal(("final", "done", false), (transcript.GetProperty("tier").GetString(), transcript.GetProperty("pass").GetProperty("status").GetString(), transcript.GetProperty("upgradable").GetBoolean()));
        Assert.All(detail.GetProperty("segments").EnumerateArray(), s => Assert.True(s.GetProperty("final").GetBoolean()));

        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync($"/api/v1/conversations/{Guid.NewGuid()}/upgrade", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsync($"/api/v1/conversations/{conversation}/upgrade?reason=bad", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Server.CreateClientWithScope("read").PostAsync($"/api/v1/conversations/{conversation}/upgrade", null)).StatusCode);
        var info = await Client.GetFromJsonAsync<JsonElement>("/api/v1/info");
        Assert.Contains("transcript-upgrade", info.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task The_route_says_why_it_refuses()
    {
        // No Soniox key and no model: nothing to pay with.
        StartServer();
        await Db.ResetAsync();
        await Server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));
        await Server.RunJobsAsync();
        var conversation = await Db.ScalarAsync<Guid>("select id from conversations");

        var response = await Client.PostAsync($"/api/v1/conversations/{conversation}/upgrade", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((HttpStatusCode.Conflict, TranscribeConversationHandler.NoEngine), (response.StatusCode, body.GetProperty("reason").GetString()));

        // Audio gone.
        StartServer(s => s["Nytka:Stt:SonioxApiKey"] = "test-key");
        await Db.ExecuteAsync("delete from speech_audio");
        body = await (await Client.PostAsync($"/api/v1/conversations/{conversation}/upgrade", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ConversationEndpoints.AudioGone, body.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_spent_budget_stops_the_route_and_parks_a_queued_pass()
    {
        var (conversation, draft, _) = await DraftedAsync(s => s["Nytka:Stt:MonthlyBudgetUsd"] = "0.0001");
        await Db.ExecuteAsync("insert into transcription_usage (batch_id, at, engine, audio_ms, text_chars, cost_usd) values (-1, now(), 'soniox', 1, 1, 1)");

        var response = await Client.PostAsync($"/api/v1/conversations/{conversation}/upgrade", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(TranscribeConversationHandler.BudgetSpent, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());

        // A job queued before the budget ran out sends nothing either.
        await Server.Get<JobQueue>().EnqueueAsync(
            JobKinds.TranscribeConversation, new TranscribeConversationPayload(conversation, "manual"), JobKinds.TranscribeConversationKey(conversation),
            Server.Time.GetUtcNow(), default);
        var calls = Server.Soniox.Calls.Count;
        await Server.RunJobsAsync();

        Assert.Equal(("waiting", TranscribeConversationHandler.BudgetSpent), await Row<(string, string)>("select status, error from transcript_passes"));
        Assert.Equal(calls, Server.Soniox.Calls.Count);
        Assert.Equal([draft], await Db.QueryAsync<long>("select id from segments"));
    }

    private async Task<(long Pass, PassRow Row)> PlannedPassAsync(Guid conversation)
    {
        var batch = await Db.ScalarAsync<long>("select id from transcription_batches");
        var map = new Nytka.Audio.Batching.OffsetMap([new Nytka.Audio.Batching.OffsetMap.Entry(0, StartMs)], 1_000);
        var id = await Server.Get<TranscriptPassStore>().CreateAsync(
            new NewPass(conversation, "manual", [batch], "[]", map.ToJson(), 1_000), Server.Time.GetUtcNow(), default);
        return (id!.Value, (await Server.Get<TranscriptPassStore>().GetAsync(id.Value, default))!);
    }

    private static PaidSegment Paid(long fromMs, long toMs, string text, SegmentVoice? voice = null) =>
        new(DateTimeOffset.FromUnixTimeMilliseconds(StartMs + fromMs), DateTimeOffset.FromUnixTimeMilliseconds(StartMs + toMs), text, "SPEAKER_1", null, voice);

    [Fact]
    public async Task A_throw_in_the_middle_of_the_swap_rolls_every_write_back()
    {
        var (conversation, draft, _) = await DraftedAsync();
        var (_, row) = await PlannedPassAsync(conversation);
        var store = Server.Get<TranscriptPassStore>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SwapAsync(
            row, [Paid(0, 1_000, "paid")], "soniox", false, Server.Time.GetUtcNow(), default,
            recorded: (_, _, _, _, _, _, _, _) => throw new InvalidOperationException("crash")));

        Assert.Equal([draft], await Db.QueryAsync<long>("select id from segments"));
        Assert.Equal(0, await Count("segment_drafts"));
        Assert.Equal(draft, await Db.ScalarAsync<long>("select evidence_segment_id from name_suggestions"));
        Assert.Equal(draft, await Db.ScalarAsync<long>("select segment_id from person_facts"));
        Assert.Equal(0, await Count("transcription_batches", "pass_id is not null"));
        Assert.Equal("planned", await Db.ScalarAsync<string>("select status from transcript_passes"));
        Assert.Equal("draft", await Db.ScalarAsync<string>("select transcript_tier from conversations"));

        var result = await store.SwapAsync(row, [Paid(0, 1_000, "paid")], "soniox", false, Server.Time.GetUtcNow(), default);
        Assert.Equal((SwapStatus.Done, 1, 1), (result.Status, result.Replaced, result.Inserted));
    }

    [Fact]
    public async Task Two_suggestions_whose_lines_become_one_line_keep_the_decided_one()
    {
        var (conversation, draft, _) = await DraftedAsync();
        var second = await Db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text)
            select conversation_id, batch_id, started_at + interval '200 milliseconds', started_at + interval '700 milliseconds', 'again'
            from segments where id = @draft returning id
            """,
            new { draft });
        await Db.ExecuteAsync(
            """
            insert into name_suggestions (id, conversation_id, target, segment_ids, name, evidence_segment_id, confidence, status, created_at)
            values (@id, @conversation, 'label', array[@second]::bigint[], 'Anna', @second, 0.9, 'pending', now())
            """,
            new { id = Guid.NewGuid(), conversation, second });
        var (_, row) = await PlannedPassAsync(conversation);

        await Server.Get<TranscriptPassStore>().SwapAsync(row, [Paid(0, 1_000, "paid")], "soniox", false, Server.Time.GetUtcNow(), default);

        var kept = Assert.Single(await Db.QueryAsync<(string Status, long Evidence)>("select status, evidence_segment_id from name_suggestions"));
        Assert.Equal(("rejected", await Db.ScalarAsync<long>("select id from segments")), kept);
    }

    [Fact]
    public async Task A_replaced_lines_voice_group_membership_moves_to_the_new_fingerprint()
    {
        var (conversation, draft, _) = await DraftedAsync();
        var batch = await Db.ScalarAsync<long>("select id from transcription_batches");
        var group = Guid.NewGuid();
        await Db.ExecuteAsync(
            "insert into voice_profile (id, model, enrolled, enrolled_count, centroid, centroid_count, enrolled_at, updated_at) values (1, 'm', '\\x00', 1, '\\x00', 1, now(), now())");
        await Db.ExecuteAsync("insert into voice_groups (id, model, centroid, count, created_at, updated_at) values (@group, 'm', '\\x00', 2, now(), now())", new { group });
        await Db.ExecuteAsync(
            "insert into segment_fingerprints (segment_id, batch_id, model, fingerprint, created_at, group_id, grouped) values (@draft, @batch, 'm', '\\x00', now(), @group, true)",
            new { draft, batch, group });
        var (_, row) = await PlannedPassAsync(conversation);

        await Server.Get<TranscriptPassStore>().SwapAsync(
            row, [Paid(0, 1_000, "paid", new SegmentVoice(0.9f, null, [1f, 2f, 3f]))], "soniox", false, Server.Time.GetUtcNow(), default);

        var fingerprint = Assert.Single(await Db.QueryAsync<(Guid? Group, bool Grouped)>("select group_id, grouped from segment_fingerprints"));
        Assert.Equal((group, true), fingerprint);
        Assert.Equal(2, await Db.ScalarAsync<int>("select count from voice_groups"));
    }

    [Fact]
    public async Task A_group_left_without_a_fingerprint_goes_with_the_draft()
    {
        var (conversation, draft, _) = await DraftedAsync();
        var batch = await Db.ScalarAsync<long>("select id from transcription_batches");
        var group = Guid.NewGuid();
        await Db.ExecuteAsync("insert into voice_groups (id, model, centroid, count, created_at, updated_at) values (@group, 'm', '\\x00', 1, now(), now())", new { group });
        await Db.ExecuteAsync(
            "insert into segment_fingerprints (segment_id, batch_id, model, fingerprint, created_at, group_id, grouped) values (@draft, @batch, 'm', '\\x00', now(), @group, true)",
            new { draft, batch, group });
        var (_, row) = await PlannedPassAsync(conversation);

        await Server.Get<TranscriptPassStore>().SwapAsync(row, [Paid(0, 1_000, "paid")], "soniox", false, Server.Time.GetUtcNow(), default);

        Assert.Equal(0, await Count("voice_groups"));
        Assert.Equal(0, await Count("segment_fingerprints"));
    }

    [Fact]
    public async Task A_draft_the_paid_lines_barely_touch_stays()
    {
        var (conversation, draft, _) = await DraftedAsync();
        var (_, row) = await PlannedPassAsync(conversation);

        // The draft runs 0 to 900 ms; the paid line starts 850 ms in.
        var result = await Server.Get<TranscriptPassStore>().SwapAsync(row, [Paid(850, 3_000, "late")], "soniox", false, Server.Time.GetUtcNow(), default);

        Assert.Equal((0, 1), (result.Replaced, result.Inserted));
        Assert.Equal(["hello there", "late"], await Db.QueryAsync<string>("select text from segments order by started_at"));
        Assert.Equal(0, await Count("segment_drafts"));
        Assert.Equal(draft, await Db.ScalarAsync<long>("select evidence_segment_id from name_suggestions"));
        Assert.True(await Db.ScalarAsync<bool>("select is_user_manual from segments where id = @draft", new { draft }));
    }

    [Fact]
    public async Task A_draft_split_between_two_paid_lines_is_replaced_and_follows_the_larger()
    {
        var (conversation, draft, _) = await DraftedAsync();
        var (_, row) = await PlannedPassAsync(conversation);

        var result = await Server.Get<TranscriptPassStore>().SwapAsync(
            row, [Paid(0, 300, "one"), Paid(300, 900, "two")], "soniox", false, Server.Time.GetUtcNow(), default);

        Assert.Equal((1, 2), (result.Replaced, result.Inserted));
        var two = await Db.ScalarAsync<long>("select id from segments where text = 'two'");
        Assert.Equal(two, await Db.ScalarAsync<long>("select evidence_segment_id from name_suggestions"));
    }

    [Fact]
    public async Task A_conversation_deleted_while_its_pass_is_at_the_engine_still_gets_its_ledger_row_and_is_cleaned_up()
    {
        var (conversation, _, _) = await DraftedAsync();
        Server.Soniox.Statuses = ["processing", "completed"];
        await UpgradeAsync(conversation);
        await Server.RunJobsAsync();
        Assert.Equal(1, Server.Soniox.StoredFiles);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/conversations/{conversation}")).StatusCode);

        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();

        Assert.Equal(1, await Count("transcription_usage", "pass_id is not null"));
        Assert.Equal(0, await Count("transcript_passes"));
        Assert.Equal(0, Server.Soniox.StoredFiles);
        Assert.Equal(0, Server.Soniox.StoredTranscriptions);
    }

    [Fact]
    public async Task Without_a_soniox_key_nothing_is_upgradable_and_the_route_changes_nothing()
    {
        StartServer(s => s["Nytka:Stt:Model"] = "parakeet");
        await Db.ResetAsync();
        await Server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));
        await Server.RunJobsAsync();
        var conversation = await Db.ScalarAsync<Guid>("select id from conversations");
        var requests = Server.Stt.Requests.Count;

        var response = await Client.PostAsync($"/api/v1/conversations/{conversation}/upgrade", null);
        var detail = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversation}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(TranscribeConversationHandler.NoEngine, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());
        Assert.False(detail.GetProperty("transcript").GetProperty("upgradable").GetBoolean());
        Assert.Equal(0, await Count("jobs", "kind = 'transcribe-conversation'"));
        Assert.Equal(0, await Count("transcript_passes"));
        Assert.Equal(requests, Server.Stt.Requests.Count);
    }

    [Fact]
    public async Task A_queued_pass_whose_key_is_gone_fails_with_a_fixed_sentence()
    {
        var (conversation, draft, _) = await DraftedAsync();
        Server.Soniox.Statuses = ["processing", "completed"];
        await UpgradeAsync(conversation);
        await Server.RunJobsAsync();
        StartServer();

        Server.Time.Advance(TimeSpan.FromSeconds(10));
        await Server.RunJobsAsync();

        Assert.Equal(("failed", TranscribeConversationHandler.NoEngine), await Row<(string, string)>("select status, error from transcript_passes"));
        Assert.Equal([draft], await Db.QueryAsync<long>("select id from segments"));
    }

    [Fact]
    public async Task The_route_tells_a_pending_batch_from_gone_audio_and_a_batch_without_audio_does_not_hold_the_tier()
    {
        var (conversation, _, _) = await DraftedAsync();
        await Db.ExecuteAsync(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) select conversation_id, started_at, ended_at, 'pending', '{}', now() from transcription_batches");
        await UpgradeAsync(conversation);
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();
        Assert.Equal("mixed", await Db.ScalarAsync<string>("select transcript_tier from conversations"));

        var pending = await Client.PostAsync($"/api/v1/conversations/{conversation}/upgrade", null);
        Assert.Equal((HttpStatusCode.Conflict, ConversationEndpoints.StillTranscribing), (pending.StatusCode, (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()));

        // The pending batch finishes without speech audio: nothing is left to pay for, and the tier says final on the next swap.
        await Db.ExecuteAsync("update transcription_batches set status = 'done' where status = 'pending'");
        await Db.ExecuteAsync("update conversations set transcript_tier = 'draft'");
        var gone = await Client.PostAsync($"/api/v1/conversations/{conversation}/upgrade", null);
        Assert.Equal((HttpStatusCode.Conflict, ConversationEndpoints.AudioGone), (gone.StatusCode, (await gone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task A_batch_without_speech_audio_leaves_the_tier_final_after_a_swap()
    {
        var (conversation, _, _) = await DraftedAsync();
        await Db.ExecuteAsync(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, response, created_at) select conversation_id, started_at, ended_at, 'done', '{}', '{}', now() from transcription_batches");

        await UpgradeAsync(conversation);
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();

        Assert.Equal("final", await Db.ScalarAsync<string>("select transcript_tier from conversations"));
    }

    [Fact]
    public async Task A_planned_pass_waits_when_the_budget_ran_out_before_it_was_sent()
    {
        var (conversation, draft, _) = await DraftedAsync(s => s["Nytka:Stt:MonthlyBudgetUsd"] = "0.5");
        Server.Soniox.Fail = call => call == "POST /v1/files" ? HttpStatusCode.ServiceUnavailable : null;
        await UpgradeAsync(conversation);
        await Server.RunJobsAsync();
        Assert.Equal("planned", await Db.ScalarAsync<string>("select status from transcript_passes"));
        var calls = Server.Soniox.Calls.Count;

        // The month's money goes before the retry.
        await Db.ExecuteAsync("insert into transcription_usage (batch_id, at, engine, audio_ms, text_chars, cost_usd) values (-1, now(), 'soniox', 1, 1, 1)");
        Server.Soniox.Fail = _ => null;
        Server.Time.Advance(TimeSpan.FromSeconds(30));
        await Server.RunJobsAsync();

        Assert.Equal(("waiting", TranscribeConversationHandler.BudgetSpent), await Row<(string, string)>("select status, error from transcript_passes"));
        Assert.Equal(calls, Server.Soniox.Calls.Count);
        Assert.Equal([draft], await Db.QueryAsync<long>("select id from segments"));
    }

    [Fact]
    public async Task A_file_kept_without_its_transcription_id_is_deleted_before_the_pass_goes_out_again()
    {
        var (conversation, _, _) = await DraftedAsync();
        Server.Soniox.Statuses = ["processing", "completed"];
        await UpgradeAsync(conversation);
        await Server.RunJobsAsync();
        Assert.Equal(1, await Count("transcript_passes", "external_file_id is not null"));
        // The run died before it kept the transcription id.
        await Db.ExecuteAsync("update transcript_passes set external_id = null");

        Server.Time.Advance(TimeSpan.FromSeconds(5));
        await Server.RunJobsAsync();

        // The first pass's file is deleted (the batch's draft used file-1), and only the new one is left.
        Assert.Contains("DELETE /v1/files/file-3", Server.Soniox.Calls);
        Assert.Equal(1, Server.Soniox.StoredFiles);
    }

    [Fact]
    public async Task A_pass_that_cannot_be_submitted_gives_up_without_leaving_a_file_at_the_engine()
    {
        var (conversation, draft, _) = await DraftedAsync();
        Server.Soniox.Fail = call => call == "POST /v1/transcriptions" ? HttpStatusCode.ServiceUnavailable : null;
        await UpgradeAsync(conversation);

        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(30));
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromMinutes(2));
        await Server.RunJobsAsync();

        Assert.Equal(("failed", "Soniox answered 503."), await Row<(string, string)>("select status, error from transcript_passes"));
        Assert.Equal(0, Server.Soniox.StoredFiles);
        Assert.Equal([draft], await Db.QueryAsync<long>("select id from segments"));
    }
}
