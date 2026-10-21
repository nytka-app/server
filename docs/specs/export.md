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
7. `GET /api/v1/export/obsidian` with an admin token answers a ZIP of Markdown notes for an Obsidian vault: one per
   conversation, one per person, and a `Tasks.md` for tasks added by hand with no conversation; file names safe on
   Windows, macOS and Linux, usable in an Obsidian link (`#`, `^`, `[` and `]` are not), and unique within the export.

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
| `GET /api/v1/export/obsidian` | admin | `200 application/zip` as below. |

## Obsidian export

`GET /api/v1/export/obsidian` answers a ZIP (`application/zip`, file name
`nytka-obsidian-<yyyyMMdd-HHmmss>.zip`) built for dropping into an Obsidian vault: a Markdown note per
conversation under `Conversations/`, a Markdown note per person under `People/`, and, when any exist, a single
`Tasks.md` at the root for tasks added by hand with no conversation (`tasks.conversation_id` is nullable). Unlike
the NDJSON export there is no settings, memory, bookmark or digest line: a conversation's own tasks are on its
note, a person's own facts are on theirs, and a task with no conversation is on `Tasks.md` instead.

A **conversation** note's front matter has `date` (the day it started, UTC, `yyyy-MM-dd`), `tags` and
`people` (the names of everyone but the wearer who spoke in it, sorted), each a YAML list of quoted
strings. The body is a `# ` heading with the conversation's title (the user's, else the generated one,
else "Untitled conversation"), its started/ended times (UTC), a `## Summary` section, a `## Tasks`
section with one `- [ ]` or `- [x]` line per live task (left out when there are none), and a
`## Transcript` section with one `**HH:mm:ss** <speaker>: <text>` line per segment, oldest first; the
time is the segment's UTC capture time and the speaker is the label of
[your-voice.md](your-voice.md#which-label-wins) (`Wearer`, the linked person's name, or the provider's
own label).

A **person** note's front matter has `tags` (sorted). The body is a `# ` heading with the person's name,
a `## Facts` section with one `- ` line per live fact, and a `## Conversations` section with one
`- [[<conversation file name>|<date> — <title>]]` wiki link per conversation they spoke in (newest
first), so opening a person in Obsidian backlinks every conversation they were part of.

The **Tasks** note is a `# Tasks` heading with one `- [ ]` or `- [x]` line per live task that has no
conversation, oldest first, each with its due date (`(due <yyyy-MM-dd>)`) when set and a
`[[<person file name>|<name>]]` wiki link when it is owed to or by a known person. Left out of the ZIP
entirely when there is no such task.

**File names.** A conversation's file name is `<date> <sanitized title> <8 hex characters of its id>.md`;
a person's is `<sanitized name> <8 hex characters of its id>.md`. The 8 hex characters are the *last* 8 of
the id's unbroken hex form, not the first: every id is a UUID v7, whose first 8 characters are its
millisecond timestamp (RFC 9562), so two ids made within the same ~65 s bucket would share them, while the
last 8 sit inside the id's random bits. Sanitizing replaces any of
`< > : " / \ | ? * # ^ [ ]` or a control character with a space, collapses runs of spaces, drops a trailing dot
or space (Windows keeps neither), and prefixes a reserved device name (`CON`, `COM1`, `LPT1`, and so on)
with `_`; an empty result becomes `untitled`. The trailing id makes every file name unique without a
pass over the whole export first, so two conversations titled alike, on the same day, never collide. The
Tasks note needs no id suffix: it is the export's one and only tasks file.

## Re-importing

`POST /api/v1/import/nytka` (admin) takes the NDJSON file above, at most 100 MB like the other imports, and
writes it back: to restore a server from its export, or to move to another one. It answers `200` with what it
wrote, `{ people, conversations, tasks, memories, personFacts, bookmarks, digests, settings }`, each
`{ imported, skipped }`, plus `segments`, the number of transcript lines written.

**Idempotent.** Every record keeps its id. A record whose id is already on the server is skipped and left as
it is, and so is one whose natural key is: a person of the same name (any case), a conversation with the same
`source`/`externalId` pair (an Omi conversation imported on both servers), a task with the same text in the
same conversation, a memory with the same text, a fact with the same text about the same person. The file's
person ids map to the ones they matched, so tasks and facts land on the right person. Importing the same file
twice changes nothing the second time, and importing a file into the server it came from changes nothing.

**One transaction.** The file is read one line at a time and written in one transaction. The first line must
be a `header` with `format` `"nytka-export"`; a `version` above 1 is refused with `400` and the fixed message
"The export is from a newer Nytka: this server reads export format version 1 only." A file whose `end` line is
missing or whose counts differ from its lines was cut short: `400`, and nothing is written. So is a line that
is not JSON or lacks a required field, and a value the schema refuses (`400`, "The export holds a record this
server cannot store."). An unknown `type` is skipped, as for any reader. No error carries a part of the file.

**What comes back as it was.** Conversations with their source, times, status, title (the user's stays the
user's), summary, tags and segments; a segment's speaker label, voice id, wearer flag, speech kind (an owner's
mark stays a mark) and person. People with their note, `named`, tags and the voice ids named after them;
tasks with their person, kind, due time and done state, standalone ones included; memories; person facts;
bookmarks; digests. A restored conversation is marked as read by enrichment through its last segment, and no
event, webhook or job is raised for any record, as with the Omi import; search indexes the new rows on its own.

**What does not.** Voiceprints (the export carries only whether one existed: the person's voice is learnt
again), and audio, which the export does not carry. A generated title that the user's own title replaced is
gone. Settings are applied after the data commits, each only where the environment does not set it and it
differs from the value in effect; a value this server refuses is counted as skipped, not fatal.

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
- Incremental export (`?since=`).
