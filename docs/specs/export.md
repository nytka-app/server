# Nytka export (leaving Omi, second half): all of my data leaves in a documented format

`GET /api/v1/export` hands over everything Nytka holds for the user as one file. Together with the Omi
import (`docs/specs/v0.7.md`) this closes the "Leaving Omi" milestone of `docs/vision.md`: data goes in
from Omi and comes out of Nytka, with no vendor in between.

## Done when

1. `GET /api/v1/export` with an admin token streams the user's conversations with their transcripts,
   tasks, memories, bookmarks, digests, people with their facts and non-secret settings.
2. The file starts with a header (format name, format version, generation time) and ends with a line
   that counts each kind of record, so a cut download is recognizable.
3. Deleted tasks and memories are absent. No API key, token, token hash or webhook secret is in the
   file, and no URL.
4. A history of any length streams with memory bounded by one page of conversations; the server never
   builds the document.
5. A `read` token or none gets `403` or `401`.
6. The format is documented here and in the README, field by field.

## Format

**NDJSON** (`application/x-ndjson`, UTF-8): one JSON object per line, separated by `\n`. One JSON
array or object would be a single document: a reader would hold all of it before the first conversation
could be used, and the server would have to close brackets it cannot take back once the status line is
sent. With lines, the server writes a record as soon as it reads it, and a reader (`jq -c`, `grep`,
any language's line loop) handles a multi-gigabyte file in constant memory. Every line has a `type`
as its first key. A reader skips a `type` it does not know.

The file name offered is `nytka-export-<yyyyMMdd-HHmmss>.ndjson` (`Content-Disposition: attachment`).
Times are ISO 8601 in UTC (`2026-09-30T10:00:00Z`) except `localDate`, the user's local day as
`yyyy-MM-dd`. Ids are the server's UUIDs and stay stable: two exports of the same data carry the same
ids.

**Order.** `header`, `setting`s, `person`s, `conversation`s (oldest first), `task`s, `memory`s,
`person_fact`s, `bookmark`s, `digest`s, `end`. Within a kind, records are in time order.

**Snapshot.** The export reads inside one read-only repeatable-read transaction, so a conversation that
closes or a task that is ticked mid-download does not appear half-changed. The transaction lives as long
as the download.

### Lines

| `type` | Fields |
|---|---|
| `header` | `format` (`"nytka-export"`), `version` (an integer, `1`), `generatedAt`, `serverVersion` |
| `setting` | `key`, `value` (a string, as an environment variable would carry it) |
| `person` | `id`, `name`, `note` (your own note on the person, else null), `voiceprint` (true when a voiceprint of this person is kept; the vector itself is never exported), `voices` (the provider's speaker ids named after this person), `tags` (the tags on the person, sorted, `[]` when none), `named` (false for a person known so far only by a role: `name` is then the role's display form, as "Repairman"), `createdAt` |
| `conversation` | `id`, `source` (`nytka` or `omi`), `externalId` (the Omi id for `omi`, else null), `startedAt`, `endedAt`, `status` (`open` or `closed`), `title` (the one the app shows: the user's, else the generated one, else null), `titleEdited` (true when `title` is the user's), `summary`, `tags` (the tags on the conversation, sorted, `[]` when none), `segments` |
| `task` | `id`, `conversationId`, `personId` (the person it is owed to or by, else null), `text`, `done`, `doneAt`, `createdAt`, `updatedAt`, `kind` (`commitment` or `idea`) |
| `memory` | `id`, `text`, `source` (`ai`, `user` or `omi`), `conversationId` (null for one added by hand), `createdAt`, `updatedAt` |
| `person_fact` | `id`, `personId`, `text`, `source` (`ai` or `user`), `basis` (`said`, `about` or `mentioned` for `ai`, null for `user`), `conversationId` (null for one added by hand), `edited`, `createdAt`, `updatedAt` |
| `bookmark` | `id`, `at`, `note`, `source` (`pendant` or `app`), `createdAt` |
| `digest` | `id`, `localDate`, `headline`, `overview`, `highlights` (`[{ text, conversationId }]`), `decisions`, `openQuestions`, `createdAt` |
| `end` | `counts`: the number of lines written per `type` (`header` and `end` not counted; a kind with none is absent) |

A segment, inside `conversation.segments`, oldest first: `startedAt`, `endedAt` (capture times),
`text`, `speaker` (the provider's label, null if none), `speakerId` (the provider's voice id, null for
imported segments), `isUser` (true for the wearer by the rule in [your-voice.md](your-voice.md#which-label-wins), null when nobody says), `person` (the
name given to that voice, else null), `speechKind` (the speech kind that applies, `person`, `media` or `call`, else null: the owner's mark, else with
`speech.mode` at `on` the guess, see [speech-kind.md](speech-kind.md#which-kind-wins)) and `speechMarked` (true when the owner's mark decides it). The
format `version` stays `1`: the two keys are added and no other changes. A bookmark carries no conversation: as in the API, the one it
belongs to is found by time (30 seconds around the span).

Only live rows are written: a task, memory or person fact the user deleted is not. A conversation deleted by the
user is gone with its segments, tasks, memories and the facts taken from it. A highlight in a digest may name a conversation that
is gone.

### Settings

The `setting` lines list the settings in effect (environment, else table, else default) that are
neither secrets, nor URLs, nor keys only the environment supplies. A URL can carry a password or a
token of its own, and it says where Nytka runs, not how: `llm.baseUrl` and `stt.url` are left out,
and so are `llm.apiKey` and `stt.apiKey`. Webhooks are left out entirely: their URLs often hold a
token and their secrets sign deliveries.

## API

| Call | Scope | Effect |
|---|---|---|
| `GET /api/v1/export` | admin | `200 application/x-ndjson` as above. An error after the first byte cannot change the status, so a file without its `end` line is incomplete. |

## Re-importing

Not built. Nytka cannot read its own export back; the Omi import reads Omi's file only. The ids and the
`source`/`externalId` pair are kept stable so an importer can be idempotent later (the schema's unique
`(source, external_id)` already does this for Omi). Until then the file is for keeping, for
other tools, and for moving away.

## Not in this version

- Audio. Speech audio is large (hours of Opus per day), kept for `audio.retentionDays` only, and
  per conversation already available from `GET /api/v1/conversations/{id}/audio`. A zip with audio
  would need a second, non-streaming format; a script that loops over the conversations in the export
  and fetches each file does the same job.
- Voice groups, voiceprints, name suggestions, voice matches, calendar events and briefs: working state or
  vectors of other people's voices, not the user's data (`docs/specs/people.md`).
- Proposed tags: a proposal is the model's guess until the user accepts it, and only the accepted tag
  is in `tags` (`docs/specs/tags.md`).
- Webhooks, tokens, diagnostics, jobs, search indexes and raw transcription responses: configuration or
  working state, not the user's data.
- Importing the file into Nytka.
- Incremental export (`?since=`).
