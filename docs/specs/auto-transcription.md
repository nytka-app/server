# Nytka, Auto transcription: a free draft now, one paid pass per conversation

**Status: draft.** Today every batch of 5 to 45 s of speech goes to the configured engine on its own. With a paid
engine that is the expensive way to buy text: the engine bills a fixed block of context text with every request, and a
heavy day sends about 600 requests. This spec replaces that with one setting, `stt.mode`, whose default **Auto**
transcribes every batch with a free engine on the server (the **draft**, also called the live pass), then sends each
conversation's kept speech to the paid engine once (the **paid pass**) and swaps the draft for its text. The other value
is **Off**: draft only, nothing paid. There is no Frugal, Balanced or Best menu: Auto decides per conversation, and the
monthly budget of the usage spec (`usage.md`, NYTKA-88, in progress) bounds it.

Auto is built from five parts, each a PR (Steps):

1. **Two passes.** Draft per batch; one paid pass per settled conversation, swapped in atomically.
2. **Pay for what is read.** A conversation Auto skips keeps its draft and its audio; it gets the paid pass when
   someone reads it, or when it turns out to matter (tasks, a meeting, a named person).
3. **Categorize, then route.** Every stretch of speech gets a category (wearer, known person, unknown person, media,
   call, noise); only some categories go to the paid engine.
4. **Clean the audio first.** The paid engine hears only the routed stretches, optionally denoised, with an offset map
   that keeps every time a capture time.
5. **Spend within the budget.** Auto reads the ledger and steps down before the cap, never past it.

The phone may supply the draft itself (on-device transcription, an optional app setting); the server treats that text
as the live pass.

## Done when

1. With `stt.mode` at `auto` (the default) and both engines configured, every batch is transcribed by
   `stt.liveModel` and costs nothing in the ledger; no batch goes to the paid engine.
2. A conversation that settles (Close, below) and passes the eager rule gets exactly one paid request per piece of at
   most `stt.auto.pieceMinutes` of routed speech, and its draft lines over the routed stretches are replaced by the
   paid text in one transaction. A crash at any point leaves either the whole draft or the whole swap.
3. After a swap, the owner's marks (`is_user_manual`, `speech_manual`, `person_id`), name suggestions, person facts and
   voice matches point at the new lines; a rejected name is still never suggested again; summary, tasks, memories,
   names and facts run again on the final text; `transcript.upgraded` is delivered once.
4. A conversation Auto skipped keeps its draft and its speech audio until `stt.auto.draftAudioDays`; opening it in the
   app, an MCP `get_conversation`, an `ask` that cites it, a bookmark or tag on it, a task from it, a calendar meeting
   over it or a named person in it queues its paid pass. The app shows "Draft" and "Upgrading" until the swap.
5. Lines whose category is `media` or `noise` never reach the paid engine; `unknown` and `call` reach it only when the
   conversation involves the wearer. Every line carries its category; the owner corrects it with the existing marks,
   and a correction that makes a line payable queues a pass for it.
6. Times of paid lines are capture times: a line the paid engine places at 12.40 s of the cut audio lands where those
   samples were captured, checked by a test that cuts, transcribes with a fake engine and maps back.
7. Near the monthly budget Auto steps down in the order of Budget, and with the budget spent it sends nothing paid. The
   ledger has one row per paid request, with its conversation.
8. `POST /api/v1/drafts` takes the phone's own text for captured audio; a batch the phone's drafts cover is not sent
   to the live engine.
9. With `stt.mode` at `off`, nothing paid is ever sent, and the transcript is the live engine's.
10. No audio, transcript text or key reaches a log, an error column or the ledger (invariant 5).

## What exists today

Verified against `main` at `e9aae05` (2026-10-21), the in-progress NYTKA-88 work in the `stt-usage-budget-a5eb5b`
worktree (not merged), and `homelab-gitops` `stt/server.py` at `a3d150f`.

- **Batching.** `SpeechBatcher` (`src/Nytka.Audio/Batching/SpeechBatcher.cs`) closes a batch at a pause of
  `PauseMs` (1 s) once it holds `MinSpeechMs` (5 s), keeps growing past `MaxSpeechMs` (30 s) until such a pause, and cuts
  at `HardMaxSpeechMs` (45 s) (`BatchRules.cs`). The rules are code constants, not settings.
- **One request per batch.** `TranscribeHandler` sends the batch WAV through `TranscriptionClient.TranscribeAsync`
  (`multipart`, `model` = `Nytka__Stt__Model`, `prompt` = `stt.vocabulary` plus the named people), maps segment times
  through the batch's `OffsetMap`, fingerprints the segments for the wearer (`VoiceMatcher`), and stores everything in
  `BatchStore.CompleteAsync` in one transaction, dropping the WAV. The batch's Opus frames stay in `speech_audio` for
  `audio.retentionDays` (14), about 14 MB per hour of speech.
- **The maintainer's engine.** `Nytka__Stt__Url` is the `stt` service and `Nytka__Stt__Model=soniox` pins Soniox.
  `stt` runs Parakeet TDT 0.6B v3 (int8, behind Silero VAD) on **every** request first (`server.py`, `inference`), so
  the free draft is already computed for each batch and thrown away. On a pinned Soniox request it uploads the WAV to
  Soniox's async API (`stt-async-v5`, language hints uk/ru/en, diarization) with a fixed `context` of about 700
  characters (`SONIOX_CONTEXT`); `stt` ignores Nytka's `prompt` field. It splits Soniox's tokens into segments and labels
  each with its own speaker fingerprints (`label_chunk`: `speaker_id`, `is_user`). A request pinned to `parakeet`
  returns before labelling, so Parakeet's segments carry no speaker. Pinned requests wait 100 s (PR #895, after 41 of
  about 803 Soniox answers were billed and then discarded at the old 7 s deadline). `stt` has `mem_limit: 2g` and idles
  at about 1.3 GiB.
- **Conversations.** `ConversationStore.AssignAsync` puts a batch into any conversation whose speech is within
  `Nytka__Conversations__Gap` (2 min) and merges conversations a late batch bridges; `CloseIdleAsync` closes those
  idle past the gap. A closed conversation reopens when stored audio from the pendant arrives late. A merge resets the
  AI and memory runs (`MergeIntoAsync`), which is the precedent this spec follows for a swap.
- **Re-runs follow segment ids.** Enrichment, memories, names and facts record the highest segment id they read
  (`ai_through_segment_id`, `memory_runs`, `people_runs`); new lines with higher ids make them stale. What points at a
  segment row: `segment_fingerprints` (cascade), `name_suggestions.evidence_segment_id` (cascade) and `segment_ids`,
  `person_facts.segment_id` (set null), `voice_matches.segment_ids` (`0019_voice_groups.sql`).
- **Speech kind.** [speech-kind.md](speech-kind.md) guesses `person`, `media`, `call` or `unsure` per line after a
  conversation closes (`classify-speech`), from structure, YAMNet and the phone's context ranges. The wearer is
  [your-voice.md](your-voice.md)'s label rule; people and voiceprints are [people.md](people.md).
- **The ledger (NYTKA-88, in progress).** `transcription_usage` holds one row per answered **batch**
  (`batch_id not null`, unique), priced by `stt.prices` (`PriceTable.Default`: Soniox audio $1.50 per million tokens at
  30,000 tokens an hour, text in and out $3.50 per million, 390 context tokens a request, 3 characters a token, fitted to
  the real bill within one cent). `stt.monthlyBudgetUsd`, `stt.budgetWarningPercent` and `stt.budgetModel` (default
  `parakeet`) switch batches to the budget model once the month's spend reaches the budget; `usage.budget.warning` and
  `usage.budget.reached` are events. Migration `0036_transcription_usage.sql`.
- **No star.** Nytka has bookmarks (a moment) and tags, no starred conversation.

## The numbers

Measured on the maintainer's server over the 24 days to 2026-10-21 (aggregates only):

| Measure | Value |
|---|---|
| Heaviest days | 614 batches, 3.1 h of batch time, 10 conversations (Oct 20); 781 batches, 3.9 h, 3 conversations (Oct 11) |
| Batch length | 14 to 21 s on a normal day (mean per day) |
| Conversations | 223; median 2 min, mean 13.5 min, longest 358 min |
| Line time by kind | wearer 58%, other people 39%, `media` guess 0.5%, `unsure` 0.8%, unguessed 1.7% |
| Bill | $8.10 for 4,717 requests and 16 h of speech; context 79% of it, audio 9%, text out 12% |

The cost of one paid request, by the ledger's own formula: **$0.001365** of context (390 tokens), plus **$0.045** per
hour of audio, plus about **$0.059** per hour of speech in text out (50,700 characters an hour). So the bill is the
request count: a 17 s batch pays 6.5 times more for its context than for its audio.

| Heavy day (614 batches, 3.1 h) | Requests | Context | Audio | Text out | Total | 30 days |
|---|---|---|---|---|---|---|
| Today: Soniox per batch | 614 | $0.84 | $0.14 | $0.18 | **$1.16** | $35 |
| Cheap fixes only (batches 15/45/60 s, context 120 tokens) | about 270 | $0.11 | $0.14 | $0.18 | **$0.43** | $13 |
| Auto, every conversation paid | about 15 (10 conversations, pieces of 30 min) | $0.01 | $0.14 | $0.18 | **$0.33** | $10 |
| Auto, unknown far-field speech not routed (assumed 20% of other people's time) | about 15 | $0.01 | $0.12 | $0.16 | **$0.29** | $9 |
| Auto, half the conversations skipped and a third of those read later (assumed) | about 10 | $0.01 | $0.09 | $0.12 | **$0.22** | $7 |

Two things follow. First, one request per conversation removes almost all of the waste; routing and skipping only
trim the remaining $0.10 per speech hour. Second, media routing saves almost no money here (0.5% of line time is
guessed media at the current threshold): its value is quality, because TV text stops reaching names, facts and tasks.
The skip rate and the far-field share are assumptions until the ledger and `GET /api/v1/transcript/eval` measure them.

## Close: when a conversation is settled

A conversation is **settled** when all of these hold:

1. `status` is `closed` (its last speech is older than the gap);
2. its last speech ended at least `stt.auto.settleMinutes` (10) ago, so the pendant's stored audio has had time to
   arrive;
3. no batch of it is `pending`;
4. `classify-speech` has run on it, or `speech.mode` is `off`.

A long conversation does not wait to close: once it holds `stt.auto.pieceMinutes` (30) of routed speech whose batches
ended more than `stt.auto.settleMinutes` ago, those batches get a pass as a **piece**. Pieces cut at a stretch boundary;
a 358-minute conversation becomes 12 requests, under Soniox's 300-minute file limit. A conversation that reopens after
a pass (late audio, a merge) gets a later pass over the batches no pass covered; nothing is paid twice.

## Two passes

### The live pass

Every batch goes to `stt.liveModel` (default `parakeet`) on `Nytka__Stt__Url`, as today, with the ledger row priced
at 0 (an engine the price table does not name costs nothing). The draft is a normal transcript: search, the app and
MCP read it at once. When a batch's speech is covered by phone drafts (On-device drafts), those are its text and the
live engine is not called.

`stt` labels speakers only on pinned cloud answers today. The live pass needs its labels too, so the paid pass can
carry `speaker_id` over (Swap): `homelab-gitops` changes `serve_pinned` to label Parakeet's segments as it labels the
A/B arm's. That is a `stt` change, outside this repository, and Step 1 of the Steps.

### The paid pass

`transcribe-conversation` (Audio lane, dedupe key `transcribe-conversation:{conversationId}`) runs one **pass**:

1. **Plan.** Read the conversation's lines and categories (Categorize), pick the routed stretches of the batches no
   pass covered, and write the pass row (`planned`) with its batch ids, stretches and offset map.
2. **Build.** Decode the stretches' Opus frames from `speech_audio` through `Timeline`, clean them (Clean the audio),
   join them with 300 ms of silence between stretches into one 16 kHz WAV, and record one `OffsetMap` entry per
   stretch (the existing class already maps a cut WAV to capture times).
3. **Submit.** Send it to the paid engine. With Soniox (below) the job uploads, creates the transcription, stores its
   id on the pass (`submitted`) and returns `JobOutcome.RunAgain(5 s)`; each later run polls once, so a slow answer
   never holds the Audio lane. With an OpenAI-compatible engine the request is synchronous, as today.
4. **Swap** (below), in one transaction with the ledger row.
5. **Clean up.** Delete the file and the transcription at Soniox on success, failure and give-up, as `stt` does.

A pass that fails gives up after the job's usual attempts; the draft stays, the pass is `failed`, and the app offers
"Try again" (`POST /api/v1/conversations/{id}/upgrade`).

### The paid engine

| Engine | When | How |
|---|---|---|
| Soniox, direct | `Nytka__Stt__SonioxApiKey` is set | Nytka calls `api.soniox.com` (`Nytka__Stt__SonioxBaseUrl`) itself: files, transcriptions, transcript, deletes; model `stt-async-v5`, language hints from `Nytka__Stt__Language` or `uk,ru,en`, diarization on |
| OpenAI-compatible | no Soniox key, `Nytka__Stt__Model` set and different from `stt.liveModel` | the existing `TranscriptionClient` with `model` = `stt.model`; a piece is cut to at most 10 min, since `stt`'s pinned path waits 100 s and runs Parakeet over the whole upload first |
| none | neither | Auto has no paid engine and behaves as Off; `/status` says so |

**Bring your own key.** The Soniox key is the server's own, from the environment only (invariant 8), so a self-hoster
needs no `stt` service, and the maintainer's server stops routing paid audio through `stt` (whose speaker tables keep
every voice's fingerprint indefinitely, speech-kind.md "Privacy").

**Context once.** The pass sends one `context`: `terms` from `stt.vocabulary` and the named people (as `prompt` today),
and a `general` entry for the domain. No descriptive paragraph by default: `stt.auto.contextText` (empty) adds one. At
one request per piece its cost is negligible either way; trimming matters only for per-batch paid requests (Cheap
fixes).

### Swap

In one transaction, after locking the conversation row `for update`:

1. **Still current?** If the conversation is gone, or a batch the pass covers is no longer in it (a merge moved rows),
   or a covered batch is not `done`, mark the pass `stale` and queue a new plan. A merge that only added batches is
   fine: they are not covered.
2. **Map old to new.** Each replaced draft line maps to the paid line it overlaps most in capture time (ties: the
   earlier). Each paid line takes, from the draft lines it overlaps for at least half its length:
   - `is_user_manual`, `speech_manual` and `person_id` when all those draft lines agree;
   - `speaker_id`: Soniox numbers speakers within one request; each such speaker takes the `speaker_id` that at least
     60% of its overlapping draft time carries, otherwise none.
3. **Repoint.** `name_suggestions.evidence_segment_id` and `segment_ids`, `person_facts.segment_id` and
   `voice_matches.segment_ids` move to the mapped new ids; a suggestion whose evidence no longer says the name stays
   (it was validated once) and keeps its status, so a rejected name stays rejected.
4. **Keep the draft.** Copy the replaced lines to `segment_drafts` (for measurement and the Parakeet fine-tune test,
   How we measure it), then delete them; their fingerprints cascade.
5. **Insert** the paid lines with `pass_id`, fingerprint them from the pass WAV with `VoiceMatcher`, as
   `TranscribeHandler` does for a batch.
6. **Re-run.** Mark enrichment pending and queue it, and reset `memory_runs`, as `MergeIntoAsync` does. Names and
   facts follow by segment id. Clear the conversation's `speech_version`, so `classify-speech` guesses the new lines.
7. **Record.** Write the ledger row (`pass_id`, engine, audio, text, cost), set the pass `done` and the conversation's
   `transcript_tier`, and publish `transcript.upgraded`.

Draft lines outside the routed stretches (media, noise, unrouted far-field speech) stay as they are, with their
category. Nothing is deleted without a replacement: a stretch the paid engine returned no text for keeps its draft.

**Hold enrichment.** A conversation whose route is `eager` does not get enriched on its draft: the scheduler skips it
until its pass is `done`, `failed`, or `stt.auto.holdMinutes` (30) past settling, so the model runs once and
`conversation.ready` fires once. A skipped conversation is enriched on its draft at once and again after a later swap.

**Events.** `transcript.upgraded { conversationId, passId, engine, replaced, inserted, tier }`, published in the swap's
transaction, is a webhook (`WebhookRecorder` gains it) with no transcript text. `segment.created` is not published for
paid lines: it is per batch, and its receivers already learn of the change from `transcript.upgraded` and the
`conversation.ready` that follows. `conversation.ready` carries `transcriptTier` (`draft`, `final` or `mixed`), so a
receiver can ignore the draft one.

## Pay for what is read

At settling, Auto gives the conversation a **route**:

| Route | When | Effect |
|---|---|---|
| `eager` | the wearer speaks at least `stt.auto.minWearerSeconds` (20) or a known person at least 20 s, and the routed stretches hold at least 10 s, and the conversation is not brief (`EnrichConversationHandler.IsBrief`) | pass now |
| `skip` | otherwise (mostly media, far-field chatter, a few words) | draft stays; a trigger may pass it later |

**Triggers** for a `skip` conversation within `stt.auto.draftAudioDays`, each queuing its pass with the reason:

| Reason | Fires when |
|---|---|
| `opened` | the app calls `POST /api/v1/conversations/{id}/upgrade` when it opens the conversation; MCP `get_conversation` counts as opened (an agent reading it is a reader). Listing, search results and the REST `GET` do not |
| `ask` | `POST /api/v1/ask` cites it as a source; the answer is not delayed, the next one reads the final text |
| `bookmark`, `tag` | a bookmark falls in it, or a tag is added to it (the "star") |
| `task` | enrichment of the draft created a task from it |
| `meeting` | a `calendar_events` row overlaps it |
| `person` | a line of it belongs to a named person (label rule, `person_id` or a confirmed voice match) |
| `correction` | the owner's mark makes a stretch payable (Categorize) |

Audio is what makes a lazy pass possible, so `speech_audio` of a conversation that has not had every routed stretch
paid is kept for `stt.auto.draftAudioDays` (30) instead of `audio.retentionDays`; once passed, the normal retention
applies. At 14 MB per speech hour and 3 h on a heavy day that is about 40 MB a day, 1.3 GB for 30 days at the
heaviest wear, usually far less. Fingerprints die with the audio, as today, so they live longer only for those
conversations.

## Categorize, then route

Auto gives every **stretch** a category. A stretch is speech-kind's unit (consecutive lines of one speaker class with
gaps under 4 s, cut at 10 s), extended to the wearer's lines, which speech-kind does not group. The category is stored
per line in `segments.category`, and categorizing needs nothing new from the audio until the noise and cleaning
research lands.

| Category | From | To the paid engine |
|---|---|---|
| `wearer` | the label rule makes the line the wearer's | yes |
| `person` | a known person: `person_id`, the voice's person (`person_voices`), or a voice match at or above `people.voiceThreshold` | yes |
| `unknown` | kind `person` or `unsure` (or no guess), none of the above | only when the conversation involves the wearer: at least `stt.auto.minWearerSeconds` of wearer speech, and the stretch within 60 s of a wearer line (speech-kind's `d`) |
| `call` | speech kind `call` | as `unknown` |
| `media` | speech kind `media` (the mark, else the guess at or above `speech.mediaThreshold`, in every `speech.mode` but `off`) | no |
| `noise` | the live engine returned no text for it, or (pending research) a non-speech classifier says so | no, unless it lies between two routed stretches with gaps under 4 s |

Speech-kind's person-first rule holds: `unsure` and a missing guess route like a person, and a stretch whose category
cannot be decided is paid. Routing uses the guess even in `speech.mode` `shadow`: it hides nothing (the media line keeps
its draft text and stays in every reader), it only decides which audio is bought.

**Corrections.** The owner already corrects with `PATCH /api/v1/segments/{id}` (`isUser`, `speechKind`, `personId`)
and the bulk `POST /api/v1/conversations/{id}/speech`. `speechKind` gains `noise`. A correction changes the line's
category at once; when it makes a stretch payable (media flipped to person, noise to person), the conversation's next
pass covers that stretch (reason `correction`). Every correction against Auto's category is a label for the routing
evaluation (`GET /api/v1/transcript/eval`), as marks are for speech kind.

**Pending research** (`nytka/research`, "audio cleaning and categorization", in progress): a classifier for `noise` and
for better `media` and far-field separation. Its result plugs in as one more signal per stretch, behind
`stt.auto.classifier` (`off` until chosen). It is accepted when, on the owner's labelled stretches, it raises media and
noise recall without calling more than 1 in 100 labelled person stretches media or noise; costs at most 60 CPU seconds
per hour of speech on the server; and fits the memory of the process it runs in (Nytka's 640 MiB, or `stt`'s 2 GiB with
1.3 GiB already used).

## Clean the audio

The pass WAV holds only the routed stretches, so non-speech and media are cut before anything is paid. Cutting is the
router's job alone, and the offset map records it; any further processing must keep the length of every stretch
sample for sample, so the map stays true.

| Step | Default | Status |
|---|---|---|
| Cut to routed stretches, 300 ms of silence between them | on | defined here |
| Gain normalisation per stretch (peak or loudness target) | `stt.auto.cleaning` `off` | pending research |
| Denoising | `off` | pending research: candidates and the choice belong to the research worker |

A cleaning step is accepted when it lowers word error on the owner's evaluation set (How we measure it) by at least 1
point absolute without raising it on the wearer's lines, costs at most 60 CPU seconds per hour of speech on the server,
and stays inside the memory limit of the process that runs it. Cleaning never changes the stored `speech_audio` or
playback: the owner hears what the pendant heard.

## Budget

Auto reads the month's spend and forecast from the NYTKA-88 ledger before each pass, and steps down in this order:

| State | When | Auto does |
|---|---|---|
| normal | below `stt.budgetWarningPercent` of `stt.monthlyBudgetUsd`, and the forecast within the budget | everything above |
| tight | at the warning percent, or the month's forecast (`UsageForecast.CostUsd`) above the budget | `eager` needs twice `stt.auto.minWearerSeconds`; `unknown` and `call` stretches are not routed; only `opened`, `ask` and `correction` trigger lazy passes |
| spent | the month's spend reached the budget | no paid pass; the app's "Upgrade" says the budget is spent; passes queued by triggers wait for the next month or a higher budget |

No budget set means normal always. A pass never starts if its estimated cost (price table, routed audio) would carry
the month past the budget; it waits as `spent` does. The live pass is free, so Off and spent leave the transcript as
complete as before, only less accurate. `stt.budgetModel` from NYTKA-88 keeps its meaning for `stt.mode` values other
than `auto` and for a live engine that is itself paid.

## Cheap fixes

These ship first, before Auto, and keep paying off for anyone who keeps a paid engine per batch:

1. **Bigger batches while a paid engine serves batches.** `BatchRules` becomes settings `stt.batch.minSeconds` (15),
   `stt.batch.maxSeconds` (45) and `stt.batch.hardMaxSeconds` (60), up from 5, 30 and 45, for about 270 requests
   instead of 614 on the heaviest day. Under Auto the live engine is free and the defaults stay at 5, 30 and 45 for a
   fresher draft. 60 s keeps `stt`'s peak under its limit (1.20 GiB measured on a 48 s upload; Parakeet's VAD segments
   are capped at 8 s, so the peak grows with the WAV, not the model). Speech-kind's stretch and Your voice's 30 s
   segment cap work on lines, not batches, and do not change.
2. **Context trimmed for pinned requests** (`homelab-gitops`, `stt/server.py`): Nytka's pinned Soniox requests send
   `terms` and `general` only, not the description paragraph; 390 tokens become about 120. Checked first on 50 stored
   batches that the word error does not rise (How we measure it).
3. **Bring your own Soniox key** and the direct client (The paid engine), usable per batch before Auto exists.

## On-device drafts

The Android app may transcribe on the phone (an optional setting; a separate spike chooses the model). The phone then
sends its text, and the server treats it as the live pass.

`POST /api/v1/drafts { items: [{ id, sessionId, startedAt, endedAt, text, engine, language? }] }`, `admin` scope,
up to 200 items, each a stretch the phone transcribed: `id` is the client's (a retry is skipped), `sessionId` the
capture session of the chunks it covers, `startedAt` and `endedAt` the capture times the chunks carry (the phone reads
them from the same frame clock, never from sample counts), `text` at most 2,000 characters, `engine` a short name
(`phone:parakeet`, `phone:whisper-small`). `200 { accepted, skipped }`; `400` names the bad field.

When `transcribe` runs a batch, it takes the drafts of the batch's session that overlap the batch. If they cover at
least 90% of the batch's speech time, they are the batch's segments (clipped to the batch, times as sent) and the live
engine is not called; the ledger row has engine `phone` and costs 0. Otherwise the live engine runs as usual: a batch
never waits for a draft that may not come, so the app sends a draft before or with the chunks it covers. Your voice's
wearer match runs on the batch WAV either way. Phone drafts are kept with the chunk rows (7 days after processing) and
are replaced by the paid pass like any draft. `/api/v1/info` `features` gains `phone-drafts`.

## Storage

Migrations, numbers fixed by this plan (NYTKA-88 holds 0036):

- `0037_transcript_passes.sql`:
  - `transcript_passes`: `id bigint` identity, `conversation_id uuid` references conversations on delete cascade,
    `reason text` (`settled`, `piece`, `opened`, `ask`, `bookmark`, `tag`, `task`, `meeting`, `person`, `correction`,
    `manual`), `status text` (`planned`, `submitted`, `done`, `failed`, `stale`, `waiting`), `engine text`,
    `external_id text null` (the engine's transcription id, to poll and delete), `batch_ids bigint[]`,
    `stretches jsonb` (`[{ from, to, category }]`, no text), `offset_map jsonb`, `audio_ms int`, `error text null`
    (fixed sentences only), `created_at`, `submitted_at`, `finished_at`; an index on `(conversation_id)` and a partial
    one on `(status) where status in ('planned', 'submitted', 'waiting')`.
  - `conversations` gains `auto_route text null` (`eager`, `skip`), `transcript_tier text not null default 'draft'`
    (`draft`, `final`, `mixed`) and `settled_at timestamptz null`.
  - `transcription_batches` gains `pass_id bigint null` (the pass that covered it).
  - `segments` gains `pass_id bigint null` (null: a draft line) and `category text null` (`wearer`, `person`,
    `unknown`, `call`, `media`, `noise`); `speech_manual` and `speech_kind` accept `noise`.
  - `segment_drafts`: `id bigint` (the replaced segment's id), `conversation_id`, `pass_id`, `started_at`, `ended_at`,
    `text`, `engine`; deleted with the conversation's speech audio.
  - `transcription_usage`: `batch_id` becomes nullable and gains `pass_id bigint null` (unique where not null), with a
    check that exactly one is set.
- `0038_phone_drafts.sql`: `phone_drafts`: `id uuid` (the client's), `session_id uuid`, `started_at`, `ended_at`,
  `text`, `engine`, `received_at`; an index on `(session_id, started_at)`.

## Settings

| Key | Variable | Default | Meaning |
|---|---|---|---|
| `stt.mode` | `Nytka__Stt__Mode` | `auto` | `auto`: draft per batch, paid pass per conversation; `off`: draft only |
| `stt.liveModel` | `Nytka__Stt__LiveModel` | `parakeet` | `model` sent for the live pass; empty sends batches to `stt.model` as before Auto (no second pass) |
| `stt.auto.settleMinutes` | `Nytka__Stt__Auto__SettleMinutes` | `10` | Quiet time after a conversation's last speech before its pass, 2 to 120 |
| `stt.auto.pieceMinutes` | `Nytka__Stt__Auto__PieceMinutes` | `30` | Routed speech that makes a piece of a long conversation, 5 to 240 |
| `stt.auto.minWearerSeconds` | `Nytka__Stt__Auto__MinWearerSeconds` | `20` | Wearer (or known person) speech that makes a conversation `eager` and lets unknown voices be paid |
| `stt.auto.draftAudioDays` | `Nytka__Stt__Auto__DraftAudioDays` | `30` | How long a conversation not yet paid keeps its speech audio, at least `audio.retentionDays` |
| `stt.auto.holdMinutes` | `Nytka__Stt__Auto__HoldMinutes` | `30` | How long an `eager` conversation's enrichment waits for its pass |
| `stt.auto.lazy` | `Nytka__Stt__Auto__Lazy` | `true` | Whether triggers pass skipped conversations |
| `stt.auto.contextText` | `Nytka__Stt__Auto__ContextText` | empty | A paragraph about the recordings for the paid engine's context |
| `stt.auto.cleaning` | `Nytka__Stt__Auto__Cleaning` | `off` | `off`, then the steps research accepts |
| `stt.auto.classifier` | `Nytka__Stt__Auto__Classifier` | `off` | `off`, then the classifier research accepts |
| `stt.batch.minSeconds`, `maxSeconds`, `hardMaxSeconds` | `Nytka__Stt__Batch__MinSeconds`, ... | `5`, `30`, `45` | Batch rules; 15, 45, 60 suit a paid engine per batch |

Options, from the environment only: `Nytka__Stt__SonioxApiKey` and `Nytka__Stt__SonioxBaseUrl`
(`https://api.soniox.com`). The budget keys are NYTKA-88's. Every variable goes into `.env.example` (commented out) and
`docker-compose.yml` (empty default).

## API

| Call | Scope | Effect |
|---|---|---|
| `GET /api/v1/conversations/{id}` | read | gains `transcript: { tier, route, pass: { status, reason, engine, finishedAt } \| null, upgradable }`; each segment gains `category` and `final` (it came from a paid pass) |
| `GET /api/v1/conversations` | read | items gain `transcriptTier` |
| `POST /api/v1/conversations/{id}/upgrade` | read | queues a pass (reason `opened`, or `manual` with `?reason=manual`); `202 { status }`; `200` when already final; `409 { reason }` when the budget is spent, Auto is off, or the audio is gone. Read scope on purpose: the app opens conversations with the read token |
| `PATCH /api/v1/segments/{id}` | admin | `speechKind` accepts `noise` |
| `GET /api/v1/transcript/eval?since&until&limit` | admin | `{ items: [{ segmentId, conversationId, startedAt, durationMs, category, routed, marked, final }], nextSince }`, no text |
| `GET /api/v1/transcript/pairs?since&until&limit` | admin | `{ items: [{ conversationId, startedAt, endedAt, draft, final, draftEngine, finalEngine }] }`: the draft text next to the paid text of the same time, for the owner's evaluation and the fine-tune test; read off the server by the owner only |
| `POST /api/v1/drafts` | admin | On-device drafts |
| `GET /api/v1/status` | read | gains `auto: { mode, liveEngine, paidEngine, budgetState, passesWaiting }` |

`/api/v1/info` `features` gains `auto-transcription` and `phone-drafts`.

**MCP.** `get_conversation` counts as opening (Pay for what is read) and says `Draft transcript` above a draft one;
`list_conversations` items carry `transcriptTier`.

**Export.** Segments gain `category` and `final`; the format `version` stays `1`. `segment_drafts` are not exported.

**Webhooks.** `transcript.upgraded`; `conversation.ready` gains `transcriptTier`.

## The app

- A draft conversation shows a thin banner, "Draft text from your server. Better text arrives when you open it", and
  while a pass is `planned` or `submitted`, "Upgrading…" with a spinner; the screen polls the conversation every 5 s for
  up to 3 minutes and then on pull-to-refresh. Opening a draft conversation calls `upgrade`.
- Lines left as draft inside a final conversation (media, noise) show in muted text; each line can show its category as
  a chip (Wearer, a name, Unknown, Media, Call, Noise), with the long-press menu of speech-kind gaining "Noise".
- `409` shows its reason ("Monthly transcription budget reached") with a link to the budget on the Device tab.
- The conversation list marks draft conversations with a small outlined "Draft" chip.
- Settings, Transcription: "Auto (recommended)" or "Off: on-server model only", one sentence each, and the budget from
  NYTKA-88. The on-device setting ("Transcribe on this phone") is the app spike's.

## Privacy

- **Fewer recipients.** With a Soniox key, paid audio goes from Nytka to Soniox only, not through `stt`, whose speaker
  tables keep fingerprints indefinitely. Soniox gets the routed stretches only: media, noise and unrouted far-field
  speech never leave the server, which is less than every batch today. Files and transcriptions are deleted at Soniox
  after each pass.
- **Longer audio, on purpose.** A conversation not yet paid keeps its speech audio and fingerprints for
  `stt.auto.draftAudioDays` (30) instead of 14. README's "What it stores" says so; setting it to `audio.retentionDays`
  turns lazy passes into "within 14 days".
- **Draft text kept beside the final.** `segment_drafts` holds replaced draft lines for as long as the audio; they
  never appear in search, export, MCP or webhooks, only in `transcript/pairs` (admin).
- **Phone drafts** are transcript text from the owner's phone to the owner's server, kept like chunk rows.
- **No new text in logs.** Pass errors are fixed sentences (`The paid engine answered 429.`); the Soniox key is never
  logged, returned or stored.

## How we measure it

Real voices never enter this repository (invariant 6). The owner runs these on their own server and keeps labels,
audio and results outside it.

1. **Spend.** The ledger, per day: requests, audio, cost, by engine, before and after each Step. Pass when a heavy day
   costs at most $0.40 under Auto (the table above says $0.29 to $0.33).
2. **Accuracy, draft against final.** `GET /api/v1/transcript/pairs` gives both texts for the same time. The owner
   corrects 100 stretches (at least 20 each of Ukrainian, Russian and English, and 20 noisy) by hand in a CSV keyed by
   conversation and time; word error of the draft and of the final against that is the evaluation set for cleaning,
   context trimming and the Parakeet fine-tune test. Pass for Auto: the final's word error is no worse than today's
   per-batch Soniox on the same stretches, which `stt`'s A/B log (`ab.sqlite`) still holds for the last 14 days.
3. **Routing.** `GET /api/v1/transcript/eval` joins categories, routes and the owner's marks without text. Routing is
   safe when, over the last 100 stretches routed away (media, noise, unrouted unknown) that the owner reviewed or
   marked, at most 2 held the wearer or a known person. Below that, raise the bar (`speech.mediaThreshold`,
   `stt.auto.minWearerSeconds`) before trusting the savings.
4. **Skip and lazy rates.** From `transcript_passes.reason`: the share of conversations `eager`, `skip`, and skipped
   then passed by each trigger. If more than half of skipped conversations get passed within a week, the eager rule is
   too strict and skipping only delays text.
5. **Fine-tune data.** The Parakeet fine-tune test (a separate worker) reads draft and final pairs with their audio
   from the owner's server; nothing of it is committed here.

In the repository, tests use synthetic tones, a fake Soniox server (upload, poll states, transcript with token times,
deletes, a 429, a slow answer) and a fake live engine.

## Conflicts with the code

1. **The ledger is per batch.** `transcription_usage.batch_id` is `not null` and unique (NYTKA-88). A pass is not a
   batch; `0037` makes `batch_id` nullable and adds `pass_id`, with exactly one set. NYTKA-88 is told before it merges,
   so it can take the shape in `0036` instead.
2. **Re-runs follow segment ids.** The swap inserts new ids, so every run sees new lines and re-runs; that is wanted.
   Rows that point at segment ids are repointed in the swap; the cascade on `name_suggestions` would otherwise delete
   rejected names and let them come back.
3. **`stt` labels Parakeet's lines with no speaker.** Without the `stt` change, draft lines have no `speaker_id`, and
   with Soniox direct the paid lines have only per-request speaker numbers: people named through `person_voices`
   would stop getting new lines. The carry-over in Swap needs the labelled draft; voice groups (`people.voiceMatching`)
   do not.
4. **Speech kind runs after close and changes kinds only in `on`.** Auto routes on the guess in `shadow` too; it hides
   nothing, so the shadow rule ("change nothing") holds for every reader, but which audio is bought does change.
5. **Enrichment is queued at close for every conversation** (`Scheduler.QueueEnrichmentsAsync`). The hold for `eager`
   conversations is a new condition in `DueForEnrichmentAsync`.
6. **Retention deletes speech audio by age only** (`RetentionHandler`). It now keeps the audio of conversations with
   unpaid routed stretches until `stt.auto.draftAudioDays`.
7. **`stt.budgetModel` switches batches**, and under Auto batches are already free. Under Auto the budget acts on
   passes (Budget), and `stt.budgetModel` applies only when the live engine is paid.
8. **No star exists.** Bookmarks and tags stand in for it; a star is not added.

## Out of scope

- Frugal, Balanced and Best modes; per-conversation engine choice by the owner.
- Streaming (real-time) paid transcription; the draft is the real-time text.
- Choosing the denoiser and the non-speech classifier (the research worker), the on-device model (the app spike), and
  the Parakeet fine-tune (its own worker). This spec fixes their interfaces and acceptance criteria only.
- Paying again for a conversation already final (a better engine later is a new spec).
- Changing the playback audio.

## Steps

| # | Repo | Content | Done when | After |
|---|---|---|---|---|
| 1 | homelab-gitops | `stt`: label pinned Parakeet answers; trimmed context for pinned Soniox | a pinned `parakeet` request returns `speaker_id`; WER unchanged on 50 stored batches | |
| 2 | server | Cheap fixes: batch rules as settings | a heavy day's ledger shows at most half the requests at the same speech | |
| 3 | server | Soniox direct client, BYO key, context once, ledger row per request | fake-Soniox tests pass, files deleted on every outcome | NYTKA-88 |
| 4 | server | Passes and swap: `0037`, plan/build/submit/swap, `segment_drafts`, `transcript.upgraded`, manual `upgrade` route | swap tests: atomic under a crash, marks and suggestions repointed, times true | 3 |
| 5 | server | Auto: `stt.mode`, live model, settle, pieces, categories and routes, enrichment hold, retention | a settled synthetic conversation gets one pass; media and noise never sent | 4 |
| 6 | server | Lazy triggers and budget states, `/status` | each trigger queues one pass; `spent` sends nothing | 5 |
| 7 | server | Corrections and evaluation: `noise` kind, correction passes, `transcript/eval`, `transcript/pairs` | a correction queues a pass for its stretch only | 5 |
| 8 | android | Draft banner, upgrade on open, category chips, Noise mark, Settings: Auto or Off | app tests; the 409 reason shown | 6 |
| 9 | server | On-device drafts: `0038`, `POST /api/v1/drafts`, use in `transcribe` | a covered batch makes no live request | 4 |
| 10 | server | Cleaning and classifier (pending research) | the acceptance criteria above, on the owner's set | 7, research |

## Review focus

1. **Atomic swap.** One transaction from the staleness check to the ledger row; no path deletes a draft line without
   inserting its replacement in the same transaction.
2. **Times.** Paid lines map through the pass's offset map only; grep for divisions by the sample rate.
3. **Person first.** Every path that keeps audio from the paid engine is a `media` or `noise` category, an unrouted
   `unknown` or `call` stretch, or the budget; `unsure` and missing data are paid.
4. **Money.** No pass starts without a ledger estimate under the budget; nothing calls the paid engine per batch under
   Auto; Soniox files are deleted on every outcome.
5. **Nothing sensitive** in logs, pass errors, `stretches`, the ledger or `transcript/eval`.
6. **Migrations** keep their numbers.
