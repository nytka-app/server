---
title: "Architecture"
description: "How audio becomes conversations: the pendant, the app, the server's job lanes and what it keeps."
order: 6
section: "Concepts"
---

The invariants below are the ones the server's own [`CLAUDE.md`](../../CLAUDE.md) states and the
code is held to. The whole picture is in [`docs/vision.md`](../../docs/vision.md).

## The path of a recording

```text
pendant --BLE--> Nytka app --HTTPS--> Nytka server --> Postgres
(Opus frames)    (queue, upload)       |-> your transcription endpoint
                                       |-> your language model
                                       '-> your webhook receivers
```

- **Pendant.** Streams 20 ms Opus frames while the phone is connected and stores audio on its own
  card while the phone is away.
- **App.** Strips the Bluetooth headers, keeps frames in a local queue and uploads them as chunks
  (`POST /api/v1/chunks`, `application/vnd.nytka.frames.v1`). It never decodes audio. On
  reconnect it also reads the pendant's stored audio and uploads it with its capture times
  ([spec v0.3](../../docs/specs/v0.3.md)).
- **Server.** Decodes Opus, drops silence with Silero VAD, sends speech to the transcription
  endpoint, and groups segments into conversations by capture time. A language model, if set,
  adds titles, summaries, tasks and memories.

## Job lanes

`JobRunner` is the only consumer of the `jobs` table, with one runner per lane
([`src/Nytka.Server/Jobs/`](../../src/Nytka.Server/Jobs/)).

| Lane | Jobs |
|---|---|
| `Audio` | processing, transcription, voice scoring |
| `Ai` | summaries, memories, digests, names, facts, briefs |
| `Hooks` | webhook delivery, calendar sync |

A slow model or webhook receiver never delays transcription. Silero is not thread-safe and
transcription requests go out one at a time, which is why `Audio` is its own lane.

## Invariants

1. **Times are capture times.** A sample index never converts to a time by dividing by the sample
   rate, because the pendant stops sending in silence and Bluetooth drops frames. `Timeline` and
   `OffsetMap` do every conversion.
2. **One transaction per processing run.** A crash repeats work and never loses or duplicates it.
   Tests: [`ProcessSessionTests.cs`](../../tests/Nytka.Server.Tests/Pipeline/ProcessSessionTests.cs).
3. **Chunk rows outlive their audio.** A retried upload finds its row and answers `200`; retention
   deletes processed rows after 7 days
   ([`RetentionTests.cs`](../../tests/Nytka.Server.Tests/Pipeline/RetentionTests.cs)).
4. **Nothing sensitive in logs or errors.** No audio, transcript text, token, or response body from
   the transcription endpoint, the model or a webhook receiver.
5. **Events run in the caller's transaction.** A webhook delivery or extraction job exists exactly
   when its change committed.
6. **Settings resolve as environment, table, default.** See [configuration](configuration.md#how-settings-resolve).
7. **Writes never evaluate the `nytka` search configuration**, so a missing dictionary can fail
   indexing but never an insert.

## What leaves the server

Three paths, each to an address you choose ([vision](../../docs/vision.md#configuration-and-security)):
speech audio goes to the transcription endpoint, transcript text to the language model endpoint,
and titles, summaries, tasks and memories, never transcripts, to the webhooks you register. The
server keeps a webhook secret as plain text because it needs it to sign deliveries.

## What it stores

Everything is in Postgres, so `pg_dump` backs it up. A dump holds your transcripts and webhook
secrets, so keep it private. Speech audio stays for `Nytka__Audio__RetentionDays`; transcripts,
summaries, tasks and memories stay until you delete them. Tokens are kept as a hash. A voiceprint,
if you enroll one, is in the database and in no API answer, export or log. The full list is in the
[README](../../README.md#what-it-stores).

## Words used here

| Term | Meaning |
|---|---|
| Frame | One 20 ms Opus audio frame with a sequence number and capture time |
| Chunk | A batch of frames uploaded in one request |
| Conversation | Speech with no gap of two minutes or more |
| Segment | One transcribed stretch of a conversation |
| Memory | A lasting fact about you taken from a conversation |

The full glossary is in the [vision](../../docs/vision.md#glossary).
