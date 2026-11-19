# Nytka without a pendant: the phone's microphone as the recorder

The Android app records from the phone's microphone, transcribes on the phone and sends the server text
segments instead of Opus chunks. The server takes them into conversations like the pendant's audio. Epic:
[nytka-app/server#322](https://github.com/nytka-app/server/issues/322). The app side belongs in
nytka-app/android and gets its own spec there; this one fixes what the server accepts and how the result is
checked.

Verified against `main` at `4308849` on 2026-11-18.

Status: proposed. Nothing is built. Every number marked "starting value" is a first guess to be replaced
by the first measurement, not a finding.

## Done when

A script can evaluate each item. The commands name tests that the child issues add; until then the item
is open.

1. **Ingest.** `POST /api/v1/segments` stores a posted segment, skips a repeated `id`, and answers `400`
   naming the bad field for a malformed item. `dotnet test --filter "FullyQualifiedName~PhoneSegments"`
   exits 0 (#352).
2. **Conversations.** Two phone segments less than `conversations.gap` apart share one conversation; two
   segments more than the gap apart open two. The same test passes with a pendant batch in the middle of
   the range (#353).
3. **Deletion.** `DELETE /api/v1/conversations/{id}` on a phone-only conversation answers `204` and leaves
   no row in `phone_segments` or `segments`; the migration is one new numbered file that squawk accepts
   (#354).
4. **Capability.** `GET /api/v1/info` lists `phone-segments` in `features` when the endpoint is wired and
   not otherwise; `docs/reference/api.md` has the endpoint's section (#355).
5. **Accuracy.** On the reference set (below), the word error rate (WER) of the phone's text is at most
   0.25 (starting value) and at most the paid-engine WER on the same recordings plus 0.10 (starting
   value). `scripts/phone-wer.sh` prints both and exits 1 above either (#356).
6. **Battery.** Over a 60-minute run of the app recording and transcribing with the screen off, on the
   reference phone at a charge between 80% and 100%, the battery drops by at most 6 percentage points per
   hour (starting value). `scripts/phone-battery.sh` reads the discharge from `adb shell dumpsys
   batterystats` and exits 1 above the ceiling. It lives in nytka-app/android; the number is recorded in
   this spec's "Measured" section.
7. **What stays on the phone.** No request the app sends in a recording session carries audio: a test
   that records the app's outgoing requests for a session of 10 minutes finds only `POST /api/v1/segments`,
   `POST /api/v1/diagnostics` and `GET /api/v1/info`, and no body with an `audio/*` or `application/octet-stream`
   content type. The same test fails if the app's manifest declares no foreground service of type
   `microphone`. It lives in nytka-app/android.

## What exists today

- The pendant path is the only way audio arrives. The app uploads 20 ms Opus frames as chunks (`POST
  /api/v1/chunks`, [v0.1](v0.1.md), "Wire format"); the first chunk of a capture session creates its row
  in `capture_sessions`. The server decodes, drops silence, transcribes batches and writes `segments`.
- `segments.batch_id` is already nullable (`db/migrations/0010_import.sql`), because imported text has no
  batch. `ImportStore` writes such segments. `POST /api/v1/import/transcript` makes one new standalone
  conversation per file, with `source` `import`; `ConversationStore.AssignAsync` matches only
  `source = 'nytka'`, so imported text never merges (`src/Nytka.Storage/ConversationStore.cs`).
- [auto-transcription](auto-transcription.md), "On-device drafts", plans `POST /api/v1/drafts`: phone text
  for stretches the pendant's chunks also cover, used in place of the live engine. It needs chunks to
  exist, so it does not cover a session with no pendant. It is not built; this spec does not replace it.
- `GoldenScore` and the golden labels score who said a line (speaker, boundaries), not what was said.
  Nothing in the repository computes a WER. Check 5 therefore needs a new scoring piece (#356), and
  the epic's wording "WER on the voice bench" does not fit: the voice bench ([#300](https://github.com/nytka-app/server/issues/300))
  compares speaker embeddings.
- [vision](../vision.md) says the app "never touches the phone's microphone"; ROADMAP.md lists the phone
  microphone under "Decided later" and "transcription on the phone" under "Not planned". Both change in
  the PR that ships the app side, not in this one.

## Design

### The capture session without audio

A capture session is an id the app chooses and a stretch of capture times. A phone session has the same
id type and never has a row in `audio_chunks`. The server creates the `capture_sessions` row on the first
segment of a session (`started_at` is the earliest `startedAt`, `last_received_at` the request time), so
every table that points at a session works unchanged. `processed_through_at` stays null, and nothing
waits for audio: the processing job for a session skips a session that has no chunk rows.

Times are capture times, as for the pendant (CLAUDE.md, invariant 1): the phone reads the wall clock when
the microphone frame is captured, and gives each segment the start and end of its speech. A segment never
carries a time derived from a sample count and a rate.

One recorder at a time. The server does not dedupe a phone segment against a pendant batch that covers
the same minutes; the two would show as repeated text in one conversation. The app refuses to start a
phone session while the pendant streams (open question 2).

### `POST /api/v1/segments`

`admin` scope, like chunks. Body at most 512 KiB, as for chunks.

```json
{ "sessionId": "uuid", "items": [
  { "id": "uuid", "startedAt": "2026-11-18T09:00:03.120Z", "endedAt": "2026-11-18T09:00:07.480Z",
    "text": "…", "engine": "phone:parakeet", "language": "uk" } ] }
```

- `sessionId` is the capture session; `items` holds 1 to 200 segments.
- `id` is the client's UUID. A retry of the same item is skipped.
- `startedAt` < `endedAt`, both ISO 8601, no later than the request time plus one minute.
- `text` is 1 to 2,000 characters after trimming. `engine` is a short name with the prefix `phone:`
  (`phone:parakeet`); `language` is optional, an ISO 639-1 code.

Answers, in the shape of the upload answers in [v0.1](v0.1.md):

- `202 Accepted` with `{ accepted, skipped }` when at least one item is new.
- `200 OK` with the same body when every item was already held.
- `409 Conflict` when an `id` is held with other times or other text.
- `400` for a malformed body or item; the message names the field (`items[3].text`). `401` for a bad
  token, `413` above 512 KiB.

A request is one transaction: all new items are stored or none is. The text goes nowhere but the database:
no log line, no error message and no exception carries it (invariant 5).

### Storage

One migration (the next free number when #354 is built; `0071` at the time of writing):

- `phone_segments`: `id uuid` primary key (the client's), `session_id uuid` references `capture_sessions`
  on delete cascade, `segment_id bigint` unique references `segments` on delete cascade, `engine text`,
  `received_at timestamptz`. It is the idempotency ledger; the text lives in `segments` with `batch_id`
  null, so search, export, the MCP tools and enrichment read it with no change.
- `conversations.source` accepts `phone`.

Deleting a conversation cascades to `segments` and then to `phone_segments`. There is no audio row to
delete; the "deleted" claim in [v0.1](v0.1.md), "Done when" 5, holds for a phone conversation because
no audio ever existed on the server.

### Conversations

The endpoint groups the new items by capture time and calls `AssignAsync` once per group, with the same
`conversations.gap` rule as for batches. `AssignAsync` matches `source in ('nytka', 'phone')`; imports
still never match. A conversation keeps the source it was created with. Late phone segments (the app
was offline) land by their capture times, as stored pendant audio does.

After storing, the endpoint queues the jobs a finished batch queues that do not need audio
(`enrich-conversation` and the search indexing). The jobs that read speech audio (voice matching, speaker
tracks, speech kind) find no audio and leave the lines unlabelled; the app shows them as one unnamed
speaker. Which jobs those are is #353's list to settle against the code; the check is that none throws on
a conversation without `speech_audio`.

The usage ledger gets no row: nothing was bought.

### `/api/v1/info`

`features` gains `phone-segments` once the endpoint exists. The app checks it before it starts a phone
session and refuses with a fixed sentence when it is missing, as it does for `codecs`.

### What the app does (for the app's own spec)

Listed so the server's limits are known, not decided here: a foreground service of type `microphone`
(Android restricts starting one from the background, so a session starts from a visible action or a
tile), voice-activity detection on the phone so silence is never transcribed, a local queue of segments
that drains after the connection returns, and a model chosen by a spike (Parakeet is the epic's
candidate; its Ukrainian accuracy and size on a phone are UNKNOWN until measured).

### What never leaves the phone

- Audio, in any form: raw PCM, Opus, a WAV, a clip or a spectrogram.
- The model files and any embedding or fingerprint derived from the voice.
- The content of the phone's notifications, other apps and storage, and the phone's location.

What reaches the server is the segment's text, its capture times, the engine name and the language.
Check 7 enforces the first line.

## The reference set

Check 5 needs recordings with a human-written transcript. The set is 20 minutes of Ukrainian and English
speech from the maintainer's own life, recorded on the reference phone, with transcripts typed by hand.
Real voices never enter this repository (invariant 6): the recordings and transcripts stay private, the
script reads their location from `NYTKA_PHONE_REFERENCE_DIR`, and the repository holds only a 30-second
synthetic fixture (text-to-speech output) for the test that runs in CI. The paid-engine WER is taken by
sending the same recordings through the server's normal path.

WER is the word edit distance divided by the reference word count, after lower-casing and removing
punctuation. The scorer reuses nothing from `GoldenScore` except where it fits: it is a new pure function
next to it, with its own unit tests (#356).

## Measured

Empty. Each run adds a row: date, phone, model, battery points per hour, WER (phone), WER (paid engine).

## Steps

1. This spec (#351).
2. `POST /api/v1/segments` and `phone_segments` read-path tests (#352). The migration is #354's, so
   #352 and #354 land together or #354 first.
3. Conversation assignment for `source = 'phone'` (#353).
4. Migration, delete path and schema tests (#354). Moves before #352 in order.
5. `phone-segments` in `/api/v1/info` and the API reference (#355), after #352.
6. WER scorer and fixture (#356), after #352.

## Open questions

1. Is a new endpoint right, or should `POST /api/v1/drafts` grow a "no chunks" mode? This spec picks a
   new endpoint, because a draft is defined by the chunks it covers and a phone segment has none; the
   two share the `phone:` engine names.
2. A person who wears the pendant and also runs the phone recorder gets duplicated text. Refuse on the
   app, dedupe on the server by overlap, or accept it?
3. The three starting values (WER 0.25, +0.10 over the paid engine, 6 points of battery per hour) are
   guesses. Which numbers would make the phone recorder worth using?
4. Which phone is the reference phone, and who types the reference transcripts?
5. ROADMAP.md and [vision](../vision.md) both rule the phone microphone and on-device transcription out
   today. Does the epic move them, and into which stage?
