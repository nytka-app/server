# HTTP API, webhooks and MCP

The HTTP API v1, the webhooks the server sends and the MCP endpoint for AI agents (14 read tools, 3 write tools).

## API

Every request under `/api` carries `Authorization: Bearer <token>`; the Scope column says what it
needs. Errors are RFC 9457 problem details. A `400` about a field of a body adds `errors`, an object
from field to a list of messages; a search without a word, a bad chunk and a bad diagnostics body
carry a title only. Objects always carry every field, null ones too. The API is additive,
so `apiVersion` stays `1` and an app from an older version keeps working.

| Method | Path | Scope | Result |
|---|---|---|---|
| GET | `/healthz` | none | 200 when the database answers |
| GET | `/api/v1/info` | read | `{ serverVersion, apiVersion, scope, features, codecs }`; `scope` is the caller's; `codecs` lists the chunk codec values the server accepts (`[21]`, Opus 20 ms; every other id is refused with `400` naming the codec, 40 (LC3) and 50 (AAC) being the reserved ones; an older server omits the field and means `[21]`); `features` holds `offline-sync`, `people`, `review`, `briefs`, `tags`, `tag-suggestions`, `roles` (name suggestions carry roles and people have `named`), `task-kinds` (tasks have a `kind` and `GET /api/v1/notes` exists), `waiting-on` (tasks of kind `waiting_on`, and `waitingOn` on the person page), `ledger` (`GET /api/v1/people/{id}/ledger` exists), `task-create` (`POST /api/v1/tasks`, and tasks have a `due` and may have no conversation), `speech-kind` ([speech kinds](../explanation/features.md#speech-kind) on lines), `context-ranges` (the server takes [context ranges](../explanation/features.md#context-from-the-phone)), `auto-names` ([auto-applied names](../explanation/features.md#auto-applied-names) and their undo exist) and `calendar-feed` (`GET /api/v1/calendar.ics` exists, with or without a calendar feed set), `review-clips` (review items carry `clip` and `context`, and their clip route exists), `transcript-upgrade` (`POST /api/v1/conversations/{id}/upgrade` exists, and a conversation has `transcript: { tier, route, pass, upgradable }`; each segment has `final` and `category`), `usage` (`GET /api/v1/usage` and the [usage and budget](../specs/usage.md) settings exist), `quality` (`GET /api/v1/quality` exists), `summary-templates` (the [summary template](../explanation/features.md#summary-templates) routes exist, and a conversation has `summaryTemplate`), `voice` when the speaker model is there, and `voice-groups` when [voice grouping](../explanation/features.md#voice-grouping) is on and works; `briefs` is listed whether or not a calendar feed is set, because the app reads `calendar.icsUrl` from `GET /api/v1/settings` |
| GET | `/api/v1/status` | admin | `{ pendingChunks, oldestPendingAt, lastError, lastErrorAt, lastSuccessAt, ai: { configured, pending, lastError, lastErrorAt } }`; a `lastError` is set only while it is current |
| GET | `/api/v1/summary-templates` | admin | `{ enabled, maxLength, items: [{ id, text, builtIn, edited, locked }] }` for `default`, `meeting`, `lecture`, `doctor` and `call`; `enabled` is the `summary.templates.enabled` setting, `edited` whether `text` differs from `builtIn`, `locked` that the environment sets it; see [Summary templates](../explanation/features.md#summary-templates) |
| GET | `/api/v1/summary-templates/{id}` | admin | One item as above; `404` for another id |
| PUT | `/api/v1/summary-templates/{id}` | admin | Body `{ text }`, 1 to 600 characters on one line, no line breaks or control characters (`400` otherwise): sets the template and returns the item; the next summary uses it. `404` for another id; `409` when the environment sets it |
| DELETE | `/api/v1/summary-templates/{id}` | admin | Restores the built-in text and returns the item; `404` and `409` as PUT |
| GET | `/api/v1/usage?from=&to=` | admin | What transcription cost, by the price table `stt.prices` ([Usage and budget](../specs/usage.md)): `{ timeZone, from, to, days, month, budget }`. `from` and `to` are local dates (`yyyy-MM-dd`, both inclusive, at most 366 days, `to` at most today), by default the month so far. A day is `{ date, requests, audioMs, textChars, costUsd, estimated, overBudget, engines: [{ engine, requests, audioMs, costUsd }] }`, every date listed; `month` is `{ month, requests, audioMs, costUsd, forecast: { costUsd, meanDayUsd, basisDays, daysLeft } }`; `budget` is `{ monthlyUsd, warningPercent, model, state, remainingUsd, overBudgetBatches }` with `state` `none`, `ok`, `warning` or `over` |
| GET | `/api/v1/quality?from=&to=` | admin | The nightly [quality metrics](../explanation/features.md#quality-metrics): `{ timeZone, from, to, days }`. `from` and `to` are local dates (`yyyy-MM-dd`, both inclusive, at most 366 days, `to` at most today), by default the last 30 days; only measured days are listed, oldest first. A day is `{ date, measuredAt, conversations, lines, fragmentRate, untitledRate, emptyRate, wearerLineShare, wearerConversationShare, voiceScoredShare, unknownSpeakerShare, rawLabelShare, storedRawLabelShare, mediaShare, mediaLineShare, taggedShare, items: { vital, ordinary, noise, unrated }, unconfirmedMemories, voiceGroups, singletonShare }`; a rate is from 0 to 1 and null when its base is 0, `voiceGroups` and `singletonShare` are null on a backfilled day |
| POST | `/api/v1/transcriptions/retry` | admin | Transcribes every failed batch that still has its audio again, as late audio; `200 { queued }`; see [Failed batches](configuration.md#transcription-endpoints) |
| POST | `/api/v1/chunks` | admin | Stores one chunk of Opus frames (`application/vnd.nytka.frames.v1`) |
| POST | `/api/v1/diagnostics` | admin | Stores 1 to 500 diagnostics samples (JSON array, at most 256 KiB); answers `{ accepted }` |
| GET | `/api/v1/diagnostics?since=&limit=` | admin | Samples oldest first: `{ items, nextSince }`; `limit` defaults to 500, caps at 5000 |
| GET | `/api/v1/coverage?from=&to=&bucket=&limit=` | admin | The "Nothing is lost" report: `{ from, to, timeZone, bucket, totals, buckets, gaps, gapsTotal, now, warnings }`; see [Running the no-loss wear test](../how-to/operations.md#running-the-no-loss-wear-test); `bucket` is `day` (default) or `hour`, `to` defaults to now, `from` to six days before today; `limit` defaults to 500, caps at 5000; `400` for a range over 62 days (14 for hours) |
| GET | `/api/v1/conversations?before=&since=&tag=&media=&noise=&limit=` | read | `{ items, nextBefore }`, newest first; an item is `{ id, startedAt, endedAt, status, preview, title, summary, aiStatus, bookmarks, source, mediaShare, transcriptTier, noise, tags }`, `bookmarks` being a count, `source` `nytka` or `omi`, `mediaShare` the share (0 to 1) of the conversation's speech time whose [speech kind](../explanation/features.md#speech-kind) is `media`, `tags` sorted names; `since` keeps conversations that started at or after it; `tag` keeps those with that [tag](../explanation/features.md#tags) (`400` for a name that is none); `media` is `hide` (leaves out those with a `mediaShare` of 0.8 or more) or `only` (keeps just those), else `400`; `noise` (true for a closed conversation with fewer than 20 words outside media) filters the same way with `hide` or `only`; `limit` defaults to 30, caps at 100 |
| GET | `/api/v1/conversations/{id}` | read | The item without `preview`, plus `titleEdited`, `aiMessage`, `aiUpdatedAt`, `summaryTemplate` (the [summary template](../explanation/features.md#summary-templates) that wrote the summary, or null), `tags`, `tasks`, `segments` (`{ id, startedAt, endedAt, text, speaker, speakerId, isUser, personId, personName, isUserSource, wearerConfidence, speechKind, speechGuess, speechScore, speechSignals, speechMarked }`, `isUserSource` being `manual`, `voice`, `provider` or null and `wearerConfidence` 1 or 0 for your mark, the voice similarity for Nytka's verdict, else null, see [Your voice](../explanation/features.md#your-voice); `speechKind` the [speech kind](../explanation/features.md#speech-kind) that applies (`person`, `media`, `call` or null), `speechGuess` Nytka's guess (also `unsure`) with its `speechScore` and `speechSignals` (a list, empty when none), `speechMarked` true when your mark decides) and `bookmarks` (`{ id, at, note }`) |
| POST | `/api/v1/import/omi?overlapping=` | admin | Body: an Omi export file; `200` with the counts of [Import from Omi](../how-to/install.md#import-from-omi); `400` for a body that is no export; `413` above 100 MB |
| POST | `/api/v1/import/nytka` | admin | Body: a file of `GET /api/v1/export`; restores it, keeping ids, skipping what is already here; `200` with the counts of [Restore a Nytka export](../how-to/install.md#restore-a-nytka-export); `400` for a body that is no export, a newer format version, a cut file or a record the schema refuses (nothing written); `413` above 100 MB |
| POST | `/api/v1/import/transcript?format=&startedAt=` | admin | Body: an SRT, VTT or TXT transcript; see [Importing other sources](../how-to/install.md#importing-other-sources); `200 { conversationId, segments }`; `400` for a body that does not parse, or a `startedAt` with no UTC offset or outside the range a chunk's base time can hold; `413` above 100 MB |
| POST | `/api/v1/import/audio?startedAt=` | admin | Body: a WAV or Ogg Opus file; see [Importing other sources](../how-to/install.md#importing-other-sources); `202 { sessionId, durationMs }`; `415` for an unsupported content type, `400` for bad audio or a `startedAt` with no UTC offset or outside the range a chunk's base time can hold, `413` above 100 MB |
| PATCH | `/api/v1/conversations/{id}` | admin | Body `{ title }`, 1 to 120 characters, or `null` for the generated title |
| POST | `/api/v1/conversations/{id}/upgrade` | admin | Queues a [paid pass](../explanation/features.md#paid-transcript-pass) over the draft lines: `202 { status: "queued" }` (`"upgrading"` when one is running), `200 { status: "final" }` when nothing is left to pay for, `409 { reason }` when no Soniox key is set, the month's budget is spent, the speech audio is gone or a batch is still being transcribed; `?reason=manual` marks a pass you asked for (default `opened`) |
| POST | `/api/v1/conversations/{id}/enrich` | admin | Queues a summary run: `202 { aiStatus: "pending" }`; `409` while the conversation is open or no model is set |
| POST | `/api/v1/speech/backfill?force=` | admin | Queues `classify-speech` for closed conversations with unguessed lines (`force=true`: also those guessed by an older `speech_version`). `200 { queued, remaining }`: at most 500 are queued per call, `remaining` counts the rest; nothing is queued while `Nytka__Speech__Mode` is `off`; see [Speech kind](../explanation/features.md#speech-kind) |
| POST | `/api/v1/conversations/{id}/speech` | admin | Body `{ kind }`: `person`, `media`, `call`, or `null` to clear. Marks every line of the conversation that is not yours, in every mode: `200 { marked }`, the number of lines; `400` for another kind, `404` for an unknown conversation; see [Speech kind](../explanation/features.md#speech-kind) |
| DELETE | `/api/v1/conversations/{id}` | admin | Deletes it with its transcript, audio, tasks and memories |
| GET | `/api/v1/conversations/{id}/transcriptions` | admin | Raw transcription responses |
| GET | `/api/v1/conversations/{id}/audio` | read | The conversation's speech as `audio/ogg` (Opus, packed without re-encoding); pauses are not stored, so they are not played; range requests work; `404` when no speech audio is stored |
| GET | `/api/v1/conversations/{id}/audio/index` | read | `{ durationMs, runs: [{ offsetMs, startedAt, endedAt }] }`: each stretch of continuous capture and where it starts in the stream; `404` as above |
| GET | `/api/v1/tasks?status=&conversationId=&before=&kind=&limit=` | read | `{ items, nextBefore }`, newest first; a task is `{ id, conversationId, conversationTitle, conversationStartedAt, text, done, doneAt, createdAt, personId, personName, kind, due, salience }` (`salience` `vital`, `ordinary`, `noise` or null; the conversation fields are `null` for a task made by hand); `status` is `open` (default) or `done`; `kind` is `commitment` (default), `idea`, `waiting_on` or `all`, else `400`; `limit` defaults to 50, caps at 200 |
| GET | `/api/v1/notes?topic=&conversationId=&before=&limit=` | read | `{ items, nextBefore }`, newest first; a note is `{ id, conversationId, conversationTitle, conversationStartedAt, topic, points, createdAt }`; `topic` is a name (`400` for one that is none); `limit` defaults to 50, caps at 200 |
| POST | `/api/v1/tasks` | admin | body `{ text, due?, conversationId?, personId? }`, `text` 1 to 200 characters, `due` ISO 8601 or `null` (`400` otherwise); answers `201` with the task, kind `commitment`, marked edited; `404` for an unknown conversation or person, `409` when the conversation already has a task with this text, or, without a conversation, an open task made by hand has it |
| PATCH, DELETE | `/api/v1/tasks/{id}` | admin | PATCH body `{ text?, done?, personId?, due? }`, `text` 1 to 200 characters, `personId` a person id or `null` (`404` for an unknown person), `due` ISO 8601 or `null` to clear it; `409` when reopening a task made by hand that an open one repeats; DELETE answers `204` |
| GET, PATCH | `/api/v1/settings` | admin | GET: `{ items: [{ key, type, value, isSet, source, locked, default }] }`. PATCH body `{ values: { "<key>": value or null } }`, all or nothing, `null` restores the default; `400` for an unknown key or a bad value, `409` for a locked key or any API key |
| POST | `/api/v1/tokens` | admin | Body `{ name, scope }`; `201` with the token's fields and `token`, shown once; `409` for a name in use |
| GET | `/api/v1/tokens` | admin | `{ items }`, newest first, revoked ones included: `{ id, name, scope, hint, createdAt, lastUsedAt, revokedAt }` |
| DELETE | `/api/v1/tokens/{id}` | admin | Revokes it: `204` |
| POST | `/api/v1/pair` | admin | Body `{ name, url }`, `url` an absolute http or https URL of at most 500 characters; `201` with `{ id, name, createdAt, token, payload, qrCodePng }`: a fresh `admin` token, shown once, and the same [pairing QR code](../reference/configuration.md#pairing-by-qr-code) as text (`payload`) and as a PNG (`qrCodePng`, base64); `409` for a name in use |
| GET | `/api/v1/memories?before=&limit=&expired=` | read | `{ items, nextBefore }`, newest first; a memory is `{ id, text, source, conversationId, conversationTitle, conversationStartedAt, createdAt, updatedAt, salience, expiresAt }`, `source` is `ai`, `user` or `omi`, `salience` `vital`, `ordinary`, `noise` or null, `expiresAt` when the fact stops being true or null; memories past their `expiresAt` are left out unless `expired=true`; `limit` defaults to 50, caps at 200 |
| POST | `/api/v1/memories` | admin | Body `{ text }`, 1 to 300 characters; `201` with the memory; `409` when a live memory holds the fact |
| PATCH, DELETE | `/api/v1/memories/{id}` | admin | PATCH body `{ text }`; DELETE answers `204` |
| GET | `/api/v1/bookmarks?before=&limit=` | read | `{ items, nextBefore }`, newest first; a bookmark is `{ id, at, note, source, conversationId }`, `source` is `pendant` or `app`; `before` is a time and `beforeId` the id of the last item of the previous page (`nextBefore`, `nextBeforeId`), so equal times are not skipped; `limit` defaults to 30, caps at 100 |
| POST | `/api/v1/bookmarks` | admin | Body `{ id, at, note?, source }`, `id` a UUID the client makes, `at` with an explicit offset, `note` up to 200 characters; `201` with the bookmark, or `200` with the stored one when the id exists |
| PATCH, DELETE | `/api/v1/bookmarks/{id}` | admin | PATCH body `{ note }`, `null` clears it; DELETE answers `204` |
| POST | `/api/v1/context/ranges` | admin | Body `{ items: [{ id, kind, route, startedAt, endedAt }] }`, 1 to 500 items: `id` a UUID the app makes, `kind` `media` or `call`, `route` `speaker`, `earpiece`, `headset`, `bluetooth` or `other`, times with an explicit offset; `endedAt` not before `startedAt` and at most 12 hours after it, `startedAt` from 1970 on and at most 24 hours ahead of the server's clock. `200 { accepted, skipped }`, `skipped` counting ranges whose id is already stored, a repeat inside the batch included; `400` names the field and the item (`items[2].kind`), never a value, and stores none of the batch; see [Context from the phone](../explanation/features.md#context-from-the-phone) |
| GET | `/api/v1/context/ranges?since=&until=&limit=` | admin | `{ items }`, oldest start first (pass `since` for the recent ones): `{ id, kind, route, startedAt, endedAt }`; `since` keeps ranges that started at or after it and `until` those that started before it; `limit` defaults to 200, caps at 1000 |
| GET | `/api/v1/digests?before=&limit=` | read | `{ items, nextBefore }`, newest date first; a digest is `{ id, localDate, headline, overview, highlights: [{ text, conversationId }], decisions, openQuestions, createdAt }`; `before` is a date (`yyyy-MM-dd`) and keeps earlier ones, `nextBefore` is the last date of the page, set only when an earlier digest exists; `limit` defaults to 30, caps at 100 |
| GET | `/api/v1/digests/{id}` | read | One digest, as in the list |
| POST | `/api/v1/digests/run?date=` | admin | Queues a run for that local date that replaces its digest; `202 { localDate }`, `400` for a missing, malformed or future date, `409` without a model |
| GET | `/api/v1/export` | admin | Streams everything you own as NDJSON (`application/x-ndjson`); see [Export](../explanation/features.md#export) |
| GET | `/api/v1/export/obsidian` | admin | A ZIP (`application/zip`) of Markdown notes for an Obsidian vault, one per conversation and one per person; see [Export](../explanation/features.md#export) |
| GET | `/api/v1/search?q=&kinds=&tag=&limit=&offset=` | read | `{ items, nextOffset }`; a hit is `{ kind, id, score, title, snippet, at, conversationId }`; `kinds` is `conversation`, `memory` and `person` (a person's `id` is the person's, `title` the name) |
| POST | `/api/v1/webhooks` | admin | Body `{ url, events, description? }`, `description` up to 200 characters; `201` with the webhook and `secret`, shown once; `409` at 20 webhooks |
| GET | `/api/v1/webhooks` | admin | `{ items }`: `{ id, url, events, description, active, createdAt, lastDelivery }`, `lastDelivery` is `{ status, at }` or null |
| PATCH, DELETE | `/api/v1/webhooks/{id}` | admin | PATCH body with any of `url`, `events`, `description`, `active`; DELETE answers `204` and drops its deliveries |
| POST | `/api/v1/webhooks/{id}/test` | admin | Queues a `ping`: `202 { deliveryId }` |
| GET | `/api/v1/webhooks/{id}/deliveries?limit=` | admin | `{ items }`, newest first: `{ id, eventType, status, attempts, lastStatusCode, lastError, createdAt, deliveredAt }`; `limit` defaults to 30, caps at 100 |
| GET | `/api/v1/voice` | admin | `{ enrolled, enrolledAt, updatedAt, enrolledSamples, learnedSegments, modelAvailable }`; never the voiceprint |
| POST | `/api/v1/voice/enrollment?mode=` | admin | Body: one or more chunks of Opus frames back to back (`application/vnd.nytka.frames.v1`) or a 16 kHz mono 16-bit WAV (`audio/wav`), at most 120 s; `mode` is `replace` (default) or `add`. `200 { speechSeconds, samples, minAgreement }`; `422` with `reason` `too-little-speech`, `too-few-samples` or `samples-disagree` and the same three numbers; `413` over 120 s; `415` another content type; `400` unreadable audio; `409` `add` to a voiceprint of another model; `503` without the speaker model |
| POST | `/api/v1/voice/reset` | admin | Back to the enrolled voiceprint, forgetting what it learned; `200` as GET, `404` with no voice enrolled |
| DELETE | `/api/v1/voice` | admin | Forgets your voice: voiceprint, fingerprints, similarities and verdicts, and every voice group; your marks stay. `204`, also with nothing enrolled |
| GET | `/api/v1/voice/segments?since=&until=&limit=` | admin | `{ items, nextSince }`, oldest first, for choosing a threshold: `{ segmentId, conversationId, startedAt, endedAt, similarity, voiceIsUser, providerIsUser, manualIsUser }`, no text; `since` keeps segments that started after it; `limit` defaults to 500, caps at 5000 |
| PATCH | `/api/v1/segments/{id}` | admin | Body `{ isUser?, personId?, speechKind? }`, at least one: `isUser` is `true` ("this is me"), `false` or `null` (clears the mark); `personId` is a person, or `null` to clear the segment's own person, see [Speaker labels](../reference/configuration.md#transcription-endpoints); `speechKind` is `person`, `media`, `call` or `null` (clears your mark) on any line, yours included, see [Speech kind](../explanation/features.md#speech-kind); `200` with the segment as a conversation shows it; `404` for an unknown segment or person |
| GET | `/api/v1/people?tag=` | read | `{ items }` by name: `{ id, name, note, createdAt, voices, segments, lastSeenAt, factCount, tags, named }`; `tag` keeps people with that [tag](../explanation/features.md#tags) (`400` for a name that is none); `lastSeenAt` is the newest segment of the person, as on the [person page](../explanation/features.md#person-page), null when never heard; `factCount` counts their live [facts](../explanation/features.md#facts-about-people) |
| PATCH | `/api/v1/people/{id}` | admin | Body `{ name?, note? }`, at least one: `name` 1 to 80 characters (and sets `named` to true), `note` up to 500, `null` clears it; `200` with the person, `409` for a name another person has; a rename also [respells](../specs/name-spelling.md) the old name in the generated titles and summaries of the person's conversations and in their open tasks you did not edit (whole name, any case; no case endings), never your own titles or transcripts |
| GET | `/api/v1/people/{id}` | read | The [person page](../explanation/features.md#person-page): `{ id, name, note, createdAt, lastSeenAt, voices, hasVoiceprint, voiceprintSamples, conversations: [{ id, title, startedAt }], facts: [Fact], openTasks: [Task], tags, named, waitingOn: [Task] }`; `404` for an unknown person |
| GET | `/api/v1/people/{id}/ledger` | read | The [promise ledger](../explanation/features.md#promise-ledger): `{ personId, personName, owedByMe: { open: [Task], done: [Task] }, owedToMe: { open, done } }`, newest 100 per list; `404` for an unknown person |
| GET | `/api/v1/people/suggestions?status=&limit=` | read | `{ items }`, newest decided (else made) first; `limit` 1 to 200, default 200; `status` is `pending` (default), `accepted`, `rejected` or `auto_applied` ([auto-applied names](../explanation/features.md#auto-applied-names)); an item is `{ id, conversationId, target, speakerId, groupId, name, role, named, personId, confidence, sameName, evidence: { segmentId, startedAt, text }, decidedAt, clip }` (`clip` as a [review](../explanation/features.md#review) item's, the evidence line's audio, null once it is gone; `decidedAt`: when it was answered or applied, null while pending; `sameName`: pending suggestions with the same name, any case, itself included), `target` being `speaker`, `label`, `group` (`groupId` is the voice group) or `person` (the voice of a person known only by a [role](../explanation/features.md#roles); `personId` is that person); `role` is a tag name or null, and `named` false means the role alone, `name` being its display form; see [People](../explanation/features.md#people) |
| POST | `/api/v1/people/suggestions/{id}/accept` | admin | Names the voice, the batch's segments or the voice group (as its card is named), creates the person known by a [role](../explanation/features.md#roles), or renames or merges the person of a `person` target; `200` with the person; `404` for an unknown suggestion; `409` when it is no longer pending |
| POST | `/api/v1/people/suggestions/accept-by-name` `{ name }` | admin | Accepts every pending suggestion of that name (any case) for a voice, label or voice group as accept does, in one transaction, so the name is one person; role-only and `person` suggestions stay; `200` with `{ person, accepted, skipped }` (`skipped`: no longer applies, as a voice that belongs to someone else); `404` when none is pending; `400` for a missing or blank name; `409` when the pending ones disagree; see [People](../explanation/features.md#people) |
| POST | `/api/v1/people/suggestions/revalidate` | admin | Deletes pending name suggestions the model made that fail the [name checks](../explanation/features.md#people); `200` with `{ checked, removed, kept }` |
| POST | `/api/v1/people/suggestions/{id}/reject` | admin | `204`; the name is never suggested again for that voice; `404` and `409` as accept |
| POST | `/api/v1/people/suggestions/{id}/undo` | admin | Takes back an [auto-applied name](../explanation/features.md#auto-applied-names) and makes the suggestion `rejected`; `204`; `404` for an unknown suggestion; `409` when it is not `auto_applied` |
| POST | `/api/v1/people/backfill?limit=&force=` | admin | Queues [name suggestions and facts](../explanation/features.md#backfill) for summarized conversations they have not read (`force=true`: also names runs made under older checks); `limit` 1 to 1000 (default 200), `400` outside it; `200` with `{ queued: { suggestNames, facts }, skipped, remaining }`; `409` without a language model |
| GET | `/api/v1/people/voice-eval?since=&until=&limit=` | admin | `{ items, nextSince }`, oldest first, for [voice grouping](../explanation/features.md#voice-grouping): `{ segmentId, conversationId, durationMs, groupId, personId, matchPersonId, similarity }`, no text and no vector; `limit` defaults to 500, caps at 5000 |
| GET | `/api/v1/speech/eval?since=&until=&limit=` | admin | `{ items, nextSince }`, oldest first, for [speech kind](../explanation/features.md#speech-kind): `{ segmentId, conversationId, startedAt, durationMs, isUser, guess, score, signals, marked, kind }`, no text; `limit` defaults to 500, caps at 2000 |
| GET | `/api/v1/people/cards` | admin | `{ items }`, at most 4 and at most 2 per conversation, newest first, empty while voice matching is off: `{ kind, id, conversationId, conversationTitle, personId, personName, similarity, clip: { from, until }, lines: [{ segmentId, startedAt, text }] }`; `kind` is `group` ("Who is this?", no person or similarity) or `match` ("Is this Olena?"); see [Voice grouping](../explanation/features.md#voice-grouping) |
| GET | `/api/v1/people/cards/{kind}/{id}/clip` | admin | The card's clip, `audio/ogg`, at most 10 s; `404` for an unknown card or when its audio is gone |
| POST | `/api/v1/people/cards/{kind}/{id}` | admin | Body `{ personId }`, `{ name }`, `{ skip: true }` or `{ reject: true }`, exactly one; naming or confirming answers `200` with the person, skipping (7 days) or rejecting `204`; `404` for an unknown card or person; `400` for a match named with someone else |
| GET | `/api/v1/review?limit=` | read | `{ items }`, newest first, `limit` 1 to 200 (default 50): `{ kind, id, conversationId, conversationTitle, at, text, proposal: { name, personId, confidence, similarity, isUser, tag, role, named, speechKind, lines, task }, clip, context }`; `kind` is `name`, `voice` (none while voice matching is off), `label`, `tag`, `speech` or `task` (a [possible duplicate task](../explanation/features.md#duplicate-tasks), `task` being the open one: `{ id, text, conversationId, conversationTitle }`); `clip` is `{ url, startMs, endMs }` (capture times in Unix milliseconds) or null when the item has no speech or its audio is gone, `context` up to 3 lines either side of the evidence, oldest first: `[{ segmentId, speaker, personName, isUser, text, startedAt, evidence }]`, empty for a `tag`; see [Review](../explanation/features.md#review) |
| GET | `/api/v1/review/{kind}/{id}/clip` | read | The item's evidence as `audio/ogg`, 3 s either side, at most 20 s, cut by capture time; a `name` item's clip works whatever the suggestion's status; `404` for an unknown kind or item, a `tag`, or audio retention has deleted |
| POST | `/api/v1/review/{kind}/{id}/accept` | admin | `name` and `voice`: `200` with the person; `label`: stores Nytka's verdict as your mark, `204`; `tag`: `200 { tags }`, the conversation's; `task`: deletes the new task and links its conversation to the open one, `200` with the kept task; `404` for an unknown kind or item; `409` for a name, tag or task pair that is no longer pending, or a tag when the conversation has 20 |
| POST | `/api/v1/review/{kind}/{id}/reject` | admin | `204`; a `label` stores the opposite of Nytka's verdict as your mark, a `task` keeps both tasks; `404` and `409` as accept |
| DELETE | `/api/v1/people/voiceprints` | admin | Deletes every voice group, every person voiceprint and every pending voice match; segment links stay. `204` |
| GET | `/api/v1/people/{id}/facts?before=&limit=` | read | `{ items, nextBefore }`, newest first; a fact is `{ id, personId, text, source, basis, conversationId, conversationTitle, segmentId, createdAt, updatedAt }`, `source` is `ai` or `user`, `basis` is `said`, `about`, `mentioned` or null; `limit` defaults to 50, caps at 200; `404` for an unknown person |
| POST | `/api/v1/people/{id}/facts` | admin | Body `{ text }`, 1 to 300 characters; `201` with the fact (`source: user`); `409` when a live fact of the person holds it; `404` for an unknown person |
| PATCH, DELETE | `/api/v1/people/{id}/facts/{factId}` | admin | PATCH body `{ text }`, `200` with the fact; DELETE answers `204`; `404` for an unknown fact |
| GET | `/api/v1/tags?q=` | read | `{ items: [{ name, conversations, people, uses }] }`, most used first, then by name; `q` keeps names that start with it; see [Tags](../explanation/features.md#tags) |
| PUT, DELETE | `/api/v1/conversations/{id}/tags/{name}` | admin | Adds or removes one tag: `200 { tags }`, the conversation's tags sorted; removing a tag it does not have is also `200`; `400` for a name that is none, `404` for an unknown conversation, `409` when adding a 21st tag |
| PUT, DELETE | `/api/v1/people/{id}/tags/{name}` | admin | The same for a person |
| POST | `/api/v1/tags/{name}/rename` | admin | Body `{ name }`; `200` with the tag; `404` for an unknown tag; `409` when the new name is another tag's (merge instead) |
| POST | `/api/v1/tags/{name}/merge` | admin | Body `{ into }`; moves every link to `into` (created when new), drops duplicates and the old tag; `200` with `into`; `404` for an unknown tag |
| GET | `/api/v1/tags/suggestions?status=` | read | `{ items: [{ id, conversationId, personId, personName, name, createdAt }] }`, newest first, at most 200; `status` is `pending` (default), `accepted` or `rejected`, else `400`; `personId` is null for a conversation's tag; see [Tags](../explanation/features.md#tags) |
| POST | `/api/v1/tags/suggestions/{id}/accept` | admin | Adds the proposed tag to its conversation, or to its person when `personId` is set: `200 { tags }`, the item's; `404` for an unknown proposal; `409` when it is no longer pending or the item has 20 tags |
| POST | `/api/v1/tags/suggestions/{id}/reject` | admin | Keeps the proposal as rejected, so the tag is not proposed again for that conversation or person: `204`; `404` and `409` as accept |
| DELETE | `/api/v1/tags/{name}` | admin | Removes the tag from every conversation and person: `204`; `404` for an unknown tag |
| GET | `/api/v1/briefs/upcoming?minutes=` | read | `{ items }`, soonest first: the [calendar events](../explanation/features.md#calendar-briefs) not over yet that start within `minutes` (1 to 1440, default 60, clamped): `{ uid, title, startsAt, endsAt, attendees: [{ name, personId }], brief: { id, text, createdAt } or null }`; `personId` is the person whose name equals the attendee's, else null |
| GET | `/api/v1/calendar.ics` | read | An RFC 5545 calendar (`text/calendar`), the [calendar feed](../explanation/features.md#calendar-feed)'s events not over yet as `VEVENT` and your open tasks with a due date as `VTODO`; also takes a `read` token as `?token=` (see [Calendar feed](../explanation/features.md#calendar-feed) for the risk) |
| POST | `/api/v1/ask` | read | Body `{ question }`, 1 to 500 characters; `{ answer, sources }` (see [Ask](../explanation/features.md#ask)); `503` without a model, `504` on a model timeout, `502` on any other model failure |
| POST | `/mcp` | read | [MCP](#mcp) |

An unknown id is a `404`, a missing or wrong token a `401`, a token with too little scope a `403`.
`aiStatus` is `none`, `pending`, `done`, `skipped` or `failed`. The chunk format and the upload answers
are specified in [docs/specs/v0.1.md](../specs/v0.1.md), the other objects in
[v0.2](../specs/v0.2.md) and [v0.4](../specs/v0.4.md).

Diagnostics are health samples from the app (link quality, queue and upload state) that the app
sends only while its developer switch is on: no audio, no transcripts, no token. Samples older than
30 days are deleted daily; there is nothing to configure.

## Webhooks

A webhook tells another tool, such as Home Assistant, n8n or a script, that something new exists.
Add one in the app (Device, developer mode, Webhooks) or over the API:

```bash
curl -X POST https://nytka.example.com/api/v1/webhooks \
  -H "Authorization: Bearer $NYTKA_ADMIN_TOKEN" -H "Content-Type: application/json" \
  -d '{"url": "https://hooks.example.com/nytka", "events": ["conversation.ready", "task.created"]}'
```

`events` names event types, or `*` for all of them except `segment.created`, which only a webhook naming it gets. The answer holds the signing `secret`, `whsec_`
and 43 characters, once: nothing shows it again, and there is no rotation, so delete the webhook and
make a new one. The URL is `http` or `https`, at most 2,048 characters. Private addresses are allowed,
because your receivers usually sit on your own network. At most 20 webhooks. `PATCH` changes `url`,
`events`, `description` or `active`. `POST /api/v1/webhooks/{id}/test` sends a `ping`, even to an
inactive webhook, and `GET /api/v1/webhooks/{id}/deliveries` lists the log.

| Event | Sent when | `data` |
|---|---|---|
| `conversation.ready` | A summary was stored, the first one and every re-run | `{ id, startedAt, endedAt, title, summary, tasks: [{ id, text }], tags, transcriptTier }`; `transcriptTier` is `draft`, `final` or `mixed` ([Paid transcript pass](../explanation/features.md#paid-transcript-pass)) |
| `task.created` | A summary produced a new commitment | the task, as `GET /api/v1/tasks` shows it, without `personId`, `personName` and `kind`, and with `due` |
| `task.completed` | A task was completed | the task |
| `memory.created` | A memory was added, by extraction or by hand | `{ id, text, conversationId }` |
| `bookmark.created` | A bookmark was added | `{ id, at, note, source }` |
| `person.fact.created` | A fact about a person was added, by extraction or by hand | `{ id, personId, personName, text, basis, conversationId }`; `basis` and `conversationId` are null for a fact you added |
| `brief.ready` | A [calendar brief](../explanation/features.md#calendar-briefs) was made for a meeting | `{ id, title, startsAt, people: [{ id, name }], text }` |
| `segment.created` | A transcription batch stored its segments, about every 30 to 90 seconds while someone talks. Opt-in: `*` does not match it | `{ conversationId, batchId, segments: [{ id, startedAt, endedAt }] }`: ids and times only, never the text. Read the text with `GET /api/v1/conversations/{id}` and a `read` token; each segment there has `isUser`, the wearer's verdict (your mark, else the voice match, else the provider's) |
| `digest.ready` | A daily digest was made, by the schedule or on demand | `{ id, localDate, headline, overview }` |
| `transcript.upgraded` | A paid pass replaced draft lines with the engine's text (once per pass) | `{ conversationId, passId, engine, replaced, inserted, tier }`: counts only, never text; a summary follows with `conversation.ready` |
| `usage.budget.warning` | The month's transcription spend reached `stt.budgetWarningPercent` of `stt.monthlyBudgetUsd`; once a month per budget | `{ id, month, budgetUsd, spentUsd, at }`; `month` is `yyyy-MM` in the user's time zone |
| `usage.budget.reached` | The spend reached the budget: batches now go to `stt.budgetModel` ([Usage and budget](../specs/usage.md)); once a month per budget | as `usage.budget.warning` |
| `ping` | You called `test` | `{}` |

A webhook gets nothing for events from before it existed, or while it is inactive. The change and its
deliveries commit together, so a delivery exists exactly when the change did. A payload never carries
a transcript: fetch what you need with a `read` token, for example `GET /api/v1/conversations/{id}`.

```json
{
  "id": "3f0d2c6e-5b1a-5c8e-9a47-0d1b7e2c9f10",
  "type": "task.created",
  "createdAt": "2026-09-29T18:14:36.4829131Z",
  "data": {
    "id": "018f0000-0000-7000-8000-0000000000a1",
    "conversationId": "018f0000-0000-7000-8000-000000000001",
    "conversationTitle": "Planning the move",
    "conversationStartedAt": "2026-09-29T17:02:11Z",
    "text": "Call the landlord about the key by Friday",
    "done": false,
    "doneAt": null,
    "createdAt": "2026-09-29T18:14:36.4829131Z"
  }
}
```

**Check the signature.** Each request carries `Content-Type: application/json` and these headers:

| Header | Value |
|---|---|
| `Nytka-Event` | The event type |
| `Nytka-Delivery` | The delivery's id |
| `Nytka-Timestamp` | When this attempt was sent, in Unix seconds |
| `Nytka-Signature` | `v1=` and the lowercase hex HMAC-SHA256 of `{timestamp}.{body}`, keyed with the secret |

The signed text is the timestamp's digits, a dot and the exact bytes of the body; the key is the whole
secret, `whsec_` included, as UTF-8. Compute it over the raw body, before you parse the JSON: key
order and spacing in the body are not fixed, so a re-serialized copy will not match. Compare in
constant time. In Python:

```python
import hashlib
import hmac
import time


def verify(secret: str, headers: dict, body: bytes) -> bool:
    timestamp = headers["Nytka-Timestamp"]
    if abs(time.time() - int(timestamp)) > 300:
        return False  # older than five minutes: a replay, or a stuck clock
    signed = timestamp.encode() + b"." + body
    expected = "v1=" + hmac.new(secret.encode(), signed, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, headers["Nytka-Signature"])
```

By hand, `printf '%s.%s' "$TIMESTAMP" "$BODY" | openssl dgst -sha256 -hmac "$SECRET"` prints the digest
to compare. Refuse timestamps more than five minutes old. Delivery is at least once and not ordered,
so drop repeats by the payload's `id`.

**Delivery and retries.** A delivery is one `POST` with a 10-second timeout, and redirects are not
followed. Any `2xx` succeeds. Anything else, another status, a timeout or a refused connection, is
tried again after 1 minute, 5 minutes, 30 minutes, 2 hours and 12 hours, six attempts in all, and the
delivery ends `failed`. Every attempt is signed with a fresh timestamp. The log keeps the status, the
attempts, the last HTTP status and a short error such as "HTTP 500" or "timeout", never a response
body. It drops a payload once its delivery ends, and keeps the newest 200 deliveries per webhook.

## MCP

Agents read Nytka over the [Model Context Protocol](https://modelcontextprotocol.io) at
`https://nytka.example.com/mcp`. It speaks Streamable HTTP and keeps no session: `POST` only, and
`GET` and `DELETE` answer `405`. Every request needs a bearer token with scope `read` or `admin`; a
missing, wrong or revoked one gets `401`. A request with an `Origin` header, which a browser always
sends, gets `403`: MCP clients are not browsers. There is no OAuth, so a client that can only sign in
through OAuth cannot connect.

| Tool | Input | Output |
|---|---|---|
| `list_conversations` | `since?`, `before?` (ISO 8601 with an offset, or a date), `tag?`, `limit?` (1 to 50, default 20) | `{ items: [{ id, startedAt, endedAt, title, summary, preview, mediaShare, tags }], nextBefore }` |
| `get_conversation` | `id` (UUID), `transcript?` (default true), `part?` (from 1, default 1) | `{ id, startedAt, endedAt, title, summary, tasks: [{ id, text, done, personId, personName, due }], transcript, truncated, part, parts, tags }` |
| `list_tasks` | `status?` (`open` or `done`), `conversationId?`, `before?` (a task id), `kind?` (`commitment`, the default, `idea`, `waiting_on` or `all`), `limit?` (1 to 200, default 50) | `{ items: [Task], nextBefore }` |
| `list_notes` | `topic?`, `conversationId?`, `before?` (a note id), `limit?` (1 to 200, default 50) | `{ items: [Note], nextBefore }`, newest first (see [Notes](../explanation/features.md#notes)) |
| `list_memories` | `before?` (a memory id), `limit?` (1 to 200, default 50), `expired?` (default false) | `{ items: [Memory], nextBefore }`; a memory past its `expiresAt` only with `expired` |
| `list_bookmarks` | `before?` (ISO 8601 with an offset, or a date), `beforeId?` (UUID), `limit?` (1 to 100, default 30) | `{ items: [{ id, at, note, source, conversationId }], nextBefore, nextBeforeId }` |
| `list_digests` | `before?` (a date, `yyyy-MM-dd`), `limit?` (1 to 100, default 30) | `{ items: [{ id, localDate, headline, overview, highlights: [{ text, conversationId }], decisions, openQuestions, createdAt }], nextBefore }` |
| `search` | `query`, `kinds?` (a list of `conversation`, `memory` and `person`), `tag?`, `limit?` (1 to 30, default 10) | `{ items: [Hit] }` |
| `list_people` | `tag?` | `{ items: [{ id, name, lastSeenAt, facts, tags, named }] }`, most recently heard first, then by name; `facts` counts live facts; `named` is false for a person known only by a [role](../explanation/features.md#roles) |
| `get_person` | `id` (UUID) or `name` (any case), one of the two | the [person page](../explanation/features.md#person-page) as `GET /api/v1/people/{id}` returns it, without `hasVoiceprint` and `voiceprintSamples`; a tool error ("No such person.") for an unknown one |
| `get_ledger` | `id` (UUID) or `name` (any case), one of the two | the [promise ledger](../explanation/features.md#promise-ledger) as `GET /api/v1/people/{id}/ledger` returns it; a tool error ("No such person.") for an unknown one |
| `list_tags` | `query?` (names starting with it) | `{ items: [{ name, conversations, people, uses }] }`, most used first, as `GET /api/v1/tags` (see [Tags](../explanation/features.md#tags)) |
| `get_quality` | `from?`, `to?` (dates, `yyyy-MM-dd`; the last 30 days by default, at most 366) | `{ timeZone, from, to, days }`, as `GET /api/v1/quality`, which needs an `admin` token while the tool takes any |
| `ask` | `question` (1 to 500 characters) | `{ answer, sources: [Source] }`, as `POST /api/v1/ask` (see [Ask](../explanation/features.md#ask)); a tool error when no model is set or it fails |

These fourteen are read-only (`readOnlyHint`), declare an output schema and return their result as
structured content and as JSON text. A tool returns the fields its REST endpoint returns, with the
limits and defaults in the table (`search` has no `offset`). Pass `nextBefore` as `before` to read the
next page. An unknown id is a tool error ("No such conversation."); a malformed id, time, status or
kind or tag is JSON-RPC error `-32602`. The transcript has one line per segment, in UTC and without a
speaker label when there is none: `[HH:mm:ss] Speaker: text`; a [media](../explanation/features.md#speech-kind) line's speaker is `Media` and a call line's `<label> (call)`. It is cut at a line boundary after
60,000 characters into parts. `part` picks one (from 1; one outside `1` to `parts` is `-32602`),
`parts` counts them and `truncated` is true while a later part exists, so a client reads the rest with
`part: 2`, `3` and so on. Only the first 20 parts are read. `transcript` is null when it is false.

**Write tools (Plane NYTKA-81) need an `admin` token.**

| Tool | Input | Output |
|---|---|---|
| `create_task` | `text` (1 to 200 characters), `conversationId?` (UUID), `kind?` (`commitment`, the default, `idea` or `waiting_on`), `personId?` (UUID), `due?` (ISO 8601) | the created [Task](../explanation/features.md#tasks); a tool error for an unknown conversation or person, a conversation that already has a task with this text, or, without a conversation, an open task made by hand that has it |
| `complete_task` | `id` (a task id) | the completed Task, as `PATCH /api/v1/tasks/{id}` with `{ done: true }`; a tool error for an unknown id |
| `create_memory` | `text` (1 to 300 characters) | the created [Memory](../explanation/features.md#memories), as `POST /api/v1/memories`; a tool error when a live memory already holds the fact |

A `read` token's `tools/list` never names these three, and calling one with a `read` token is
JSON-RPC error `-32600` ("Access forbidden: This tool requires authorization."): the MCP host's
authorization filters check the same [`admin`-or-`read` scope](../reference/configuration.md#tokens-and-scopes) REST does, before
the tool runs. `create_task` matches `POST /api/v1/tasks` (see [Tasks](../explanation/features.md#tasks)); a task it creates is marked `edited`, so a later summary never removes or
rewrites it, and it raises `task.created` only for a `commitment`, matching a summary's tasks.

**Connect a client.** Give it the URL and a `read` token (see [Tokens and scopes](../reference/configuration.md#tokens-and-scopes)).
In Claude Code:

```bash
claude mcp add --transport http nytka https://nytka.example.com/mcp \
  --header "Authorization: Bearer nyt_..."
```

Or in a project's `.mcp.json`, with the token in an environment variable:

```json
{
  "mcpServers": {
    "nytka": {
      "type": "http",
      "url": "https://nytka.example.com/mcp",
      "headers": { "Authorization": "Bearer ${NYTKA_TOKEN}" }
    }
  }
}
```

Any client that speaks Streamable HTTP and sends a fixed `Authorization` header connects the same
way. Behind a reverse proxy, pass `/mcp` and that header through unchanged.
