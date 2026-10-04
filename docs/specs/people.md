# Nytka, people: who the other voices are, and what you know about them

The roadmap's "People" milestone, after 1.0. v0.6 lets you name a voice the transcription provider
tells apart; Your voice finds the wearer's lines. This milestone fills the gap between them in three
layers, each usable without the next:

1. **Name suggestions from text.** The model reads a conversation and suggests names for unnamed
   voices ("Привіт, я Олена", "Thanks, Marko"). No biometrics. Nothing is applied until you confirm.
2. **Voice grouping, opt-in.** Nytka groups the fingerprints of unnamed voices across
   conversations, asks "Who is this?", and keeps one voiceprint per person you confirm. Off by
   default.
3. **A knowledge map.** Facts about each person, taken only from lines whose speaker you confirmed,
   plus a person page, MCP tools and search.

Three extras ride on them: tasks name the person they are owed to, a brief before a calendar
meeting, and one review inbox for everything waiting on your answer.

## Done when

1. After a conversation is summarized, the model suggests names for its unnamed voices, each with
   the segment that shows it and a confidence. A suggestion changes no label until accepted;
   accepting it names the voice as `POST /api/v1/people` does; a rejected name is never suggested
   again for that voice.
2. With `people.voiceMatching` off (the default), Nytka computes and stores nothing about other
   people's voices beyond what Your voice already does.
3. With it on, fingerprints of unnamed voices form groups across conversations; `GET
   /api/v1/people/cards` offers at most 4 cards, at most 2 from one conversation, each with a clean
   stretch of at least 5 s and a clip of at most 10 s; a skipped card does not come back for 7 days.
4. Naming a card links its segments to the person and starts or updates that person's voiceprint. A
   later segment that matches a voiceprint at or above `people.voiceThreshold` is a suggestion until
   confirmed. Deleting a person deletes their voiceprint, their facts and every link in one
   transaction.
5. No voiceprint, group centroid or fingerprint appears in an API response, export, webhook, MCP
   answer or log.
6. Facts about a person come only from lines of confirmed speakers, or from a line that names a
   known person (marked `mentioned`). A fact already known, even one you deleted, is not added
   again; an edited fact is never rewritten.
7. `GET /api/v1/people/{id}` shows last seen, recent conversations, facts, open tasks owed to them
   and your note. MCP has `list_people` and `get_person`; search finds people by name and fact.
8. A new task carries the person it is owed to when the model names a known person.
9. With `calendar.icsUrl` set, a meeting with a known attendee gets a brief before it starts,
   delivered as `brief.ready` and listed by `GET /api/v1/briefs/upcoming`.
10. `GET /api/v1/review` lists pending name suggestions, voice matches and low-confidence wearer
    labels, and each can be accepted or rejected.
11. The evaluations in "How we measure it" pass on the owner's recordings, outside the repository.

## What exists today

Verified against `main` at `ffa73c5`:

- A voice is the provider's `speaker_id` (`segments.speaker_id`, migration 0009). A person owns
  voices through `person_voices`; a segment with no `speaker_id` can never be named. Most
  OpenAI-compatible providers return no `speaker_id`, so today most segments cannot get a name.
- The label rule lives in `SpeakerLabel` (`Nytka.Storage/PeopleStore.cs`), but the person join is
  written out again in `ConversationStore.cs:219` and `ExportStore.cs:123`. All three change
  together.
- `segment_fingerprints` exist only while the wearer has a voiceprint (`VoiceMatcher.MatchAsync`
  returns null without one), only for segments of 1 to 30 s, and die with their batch's speech
  audio (`BatchStore.DeleteSpeechAudioEndedBeforeAsync`; with `audio.retentionDays` 0, in the same
  transaction that stores the segments).
- Webhook URLs allow private and loopback addresses on purpose (`WebhookEndpoints.cs`, class
  comment); deliveries never follow redirects (`WebhooksExtensions.cs`, `AllowAutoRedirect = false`).
- Tasks are only the wearer's own commitments (`ConversationPrompt.System`): "Leave out what other
  people said they would do".

## Layer 1: name suggestions

**When.** A subscriber on `conversation.ready` queues `suggest-names` (Ai lane, dedupe key
`suggest-names:{conversationId}`) when `people.suggestNames` is on, the model is configured, the
conversation is not brief (`EnrichConversationHandler.BriefWords`), and it has at least one unnamed
voice. `people_runs` records the highest segment id read, so a repeated `conversation.ready` over
the same segments asks nothing.

**What counts as a voice.** A segment that the label rule does not make the wearer's and that has
no person yet belongs to one target:

| Target | Key | Accepting it |
|---|---|---|
| `speaker` | the provider's `speaker_id` | inserts `person_voices`, as `POST /api/v1/people { name, speakerId }` |
| `group` | a voice group (Layer 2) | as answering its card with that name |
| `label` | the provider's `speaker` label within one batch, no `speaker_id` | sets `segments.person_id` on those segments only |

A segment with none of the three (no label, no group) cannot be suggested for.

**The call.** The transcript is rendered as today, except that each unnamed target shows as
`Voice A`, `Voice B` and so on, and each line carries its segment id: `[14:03:12] #48121 Voice A:
...`. The user message lists the known people's names and `memories.userName`. The system message
asks for names only from self-introductions ("I'm", "мене звати", "я —") or from someone addressing
the voice by name in the next line or two, never from a name merely mentioned, and never the
wearer's name. Schema:

```json
{ "type": "object", "additionalProperties": false, "required": ["suggestions"],
  "properties": { "suggestions": { "type": "array", "items": { "type": "object",
    "additionalProperties": false, "required": ["voice", "name", "segmentId", "confidence"],
    "properties": { "voice": { "type": "string" }, "name": { "type": "string" },
      "segmentId": { "type": "integer" }, "confidence": { "type": "number" } } } } } }
```

**Apply.** A suggestion is dropped when its voice is not one of the letters sent, its segment is
not in the conversation, its name is empty, longer than 80 characters or equal (ignoring case) to
`memories.userName`, or its confidence is below 0.5. A name equal to a person's name carries that
person's id. At most one suggestion per target per run, the highest confidence. Rows are unique on
(target, lower(name)), rejected ones included, so a rejected name is never offered again for that
voice.

## Layer 2: voice grouping (opt-in)

**This reverses a line of Your voice.** That spec left "telling other people apart by voice" out,
because it means keeping fingerprints of people who never agreed, for longer than their audio. The
owner decided to do it, opt-in. The README says so in "What it stores", and the app shows the
setting with a sentence on the legal duty of recording people.

**Requires an enrolled wearer voice.** Nytka fingerprints segments only while the wearer has a
voiceprint ("no voiceprint, no fingerprints"), and that rule stays: without enrollment there is
nothing to group, and the setting's response says so. It also keeps the wearer out of the groups.
**Requires `audio.retentionDays` of 1 or more:** with 0, fingerprints and audio die in the
transcription transaction, before any grouping runs, so Layer 2 does nothing and `/api/v1/info`
leaves out `voice-groups`.

**Grouping.** The scheduler's scan (every minute) queues `group-voices` (Audio lane, dedupe key `group-voices`)
when `people.voiceMatching` is on and ungrouped fingerprints exist. The job takes each ungrouped
fingerprint of a segment that is not the wearer's, has no person, and whose `model` is the loaded
model's, in segment order:

1. Compared with every person's voiceprint: the best at or above `people.voiceThreshold` adds the
   segment to that person's pending **voice match** for the conversation. No label changes.
2. Otherwise compared with every group's centroid: the best at or above the threshold joins that
   group, whose centroid moves as a running mean.
3. Otherwise it starts a new group.

The fingerprint is then marked grouped. No model call; the job is CPU over 768-byte vectors.

**A group never outlives its audio.** A group's centroid is the mean of fingerprints that still
exist. When retention deletes fingerprints, groups left with none are deleted in the same
statement; `DELETE /api/v1/voice` (forget my voice) deletes every group too.

**Cards.** `GET /api/v1/people/cards` picks from pending voice matches ("Is this Olena?") and groups
without a person ("Who is this?"). A card needs a clean stretch: consecutive segments of that group
or match in one conversation, with no other speaker's segment between them, together at least 5 s,
with speech audio still stored. The clip is the stretch's first 10 s at most. A set holds at most 4
cards and at most 2 from one conversation, newest conversation first. Skipping a card hides it for 7
days (`skipped_until`). Omi uses caps of the same kind; its exact cooldown is UNKNOWN, and 7 days is
this spec's choice.

**When the audio is gone.** A group whose stretch has lost its audio is not offered as a card, and
dies with its last fingerprint. A pending voice match stays in the review inbox as text (its lines
and similarity) with no clip; confirming it still links the segments but adds no sample, since the
fingerprints are gone.

**Confirming.** Naming a group (a new or existing person) or accepting a match sets
`segments.person_id` on its segments, blends the fingerprints still held into the person's
voiceprint (weighted by count, as `VoiceStore.Blend` does for the wearer), and deletes the group, in
one transaction. A person's voiceprint is the only vector of another person that outlives audio.

**Forgetting.** `DELETE /api/v1/people/{id}` deletes the person, their voiceprint, voice links,
segment links, facts, suggestions and matches by cascade. `DELETE /api/v1/people/voiceprints`
deletes every group, every person voiceprint and every pending voice match, and keeps confirmed
segment links (statements, as the wearer's own marks are). Turning `people.voiceMatching` off stops
new work and keeps what is stored, until that call.

**No vector leaves Postgres.** As in Your voice: no route, export, webhook, MCP tool or log carries
a voiceprint, centroid or fingerprint. A person shows only `hasVoiceprint` and a sample count.

## Which label wins

Your voice's rule stays for `isUser`. For the name of a non-wearer segment, first match wins:

1. `segments.person_id`: a confirmed suggestion, card, match or manual mark on the segment.
2. The person who owns the segment's `speaker_id` (`person_voices`, v0.6).
3. The provider's `speaker`.

`SpeakerLabel.Joins` and `Column`, `ConversationStore`'s segment query and `ExportStore`'s segment
query read it one way; segments keep `personId` and `personName` as the result. `PATCH
/api/v1/segments/{id}` gains `personId` (a person or null) for marking a line by hand.

A **confirmed speaker** is a segment that is the wearer's by the rule, or has a person by step 1 or
2. Facts (Layer 3) use only these.

## Layer 3: the knowledge map

**Facts.** `person_facts` holds short facts about one person. Extraction mirrors memories (v0.4):
`extract-person-facts` (Ai lane, dedupe key per conversation) is queued on `conversation.ready` when
`people.facts` is on, the model is configured, the conversation is not brief, and it has a confirmed
non-wearer speaker or a line whose text contains a known person's name. The model gets the
transcript with segment ids, the people involved (`id: name`), up to 30 newest facts of each, and
returns at most 10 facts, each `{ personId, text, segmentId }`.

**Basis, set by the server, never by the model,** from the evidence segment:

| Evidence segment | Basis |
|---|---|
| its person is the fact's person | `said` (they said it about themselves) |
| the wearer's, or another confirmed person's | `about` |
| an unconfirmed speaker, and the line contains the person's name | `mentioned` |
| anything else | dropped |

**Rules, as memories.** Text 1 to 300 characters; a fingerprint (`TextFingerprint`) unique per
person, tombstones included, so a deleted fact never returns; editing sets `edited` and keeps the
fingerprint; extraction never rewrites a fact; a hand-made fact has `source: user` and no basis.
Deleting a conversation deletes the facts taken from it (v0.4's open decision 2). A new fact
publishes `person.fact.created`. Failures retry as `extract-memories` does (three rounds, an hour
apart), recorded in `people_runs`.

**Person page.** `people.note` holds your own note on the relationship (up to 500 characters), never
touched by a model.

## Extras

**Commitments per person.** The `enrich-conversation` task schema changes from a list of strings to
`{ text, person }`, where `person` is a name from the people the user message lists (the
conversation's confirmed speakers) or null. A name that matches one of them sets `tasks.person_id`
on insert; reconcile and fingerprints are unchanged, and a person on an existing row is never
rewritten. `PATCH /api/v1/tasks/{id}` takes `personId`. Tasks today hold only the wearer's
commitments, so `person_id` means the person the wearer owes it to or who asked for it; what others
owe the wearer is not a task and stays out (see Conflicts).

**Pre-meeting brief.** `calendar.icsUrl` points at a read-only ICS feed. `sync-calendar` (Hooks
lane, every 15 minutes from the scheduler) fetches it as webhooks are delivered: `http` or `https`,
no redirects (a 3xx fails the sync), 10 s, and a 5 MB cap on the body. The URL is never logged; a
failure logs a fixed sentence and the status code. Events of the next 48 hours, recurring ones
expanded, are stored with their attendees' display names (`CN`). An attendee matches a person when
the names are equal ignoring case. When an event with at least one matched person starts within
`calendar.briefMinutes`, `make-brief` (Ai lane, dedupe key per event and start) asks the model for a
short brief from each matched person's facts, note, open tasks and the titles and summaries of
their last 5 conversations (never transcripts), stores it and publishes `brief.ready`. Past events
and their briefs are deleted a day after they end.

**Review inbox.** `GET /api/v1/review` merges three queues, newest first: pending name suggestions,
pending voice matches, and low-confidence wearer labels (segments of the last 14 days whose
`voice_similarity` is within 0.05 of `voice.userThreshold` and that carry no manual mark, at most
20). Accepting a label stores Nytka's verdict as the wearer's mark; rejecting stores the opposite
(`VoiceStore.MarkAsync`), so neither needs a new table.

## What it stores

Migrations `0014` to `0020`, one per plan task (the plan allocates the numbers):

- `people` gains `note text null` (at most 500) and a `search tsvector`.
- `segments` gains `person_id uuid null references people on delete set null`.
- `people_runs`: `conversation_id` and `kind` (`names`, `facts`) as key, `status`,
  `through_segment_id`, `failures`, `message`, `updated_at`; cascades with the conversation.
- `name_suggestions`: `id`, `conversation_id` (cascade), `target` (`speaker`, `group`, `label`),
  `speaker_id`, `group_id` (cascade, constraint added with `voice_groups`), `segment_ids bigint[]`, `name`, `person_id` (cascade),
  `evidence_segment_id` (cascade), `confidence real`, `status` (`pending`, `accepted`, `rejected`),
  `created_at`, `decided_at`. Unique on the target and `lower(name)`.
- `person_facts`: `id`, `person_id` (cascade), `text`, `fingerprint`, `source` (`ai`, `user`),
  `basis` (`said`, `about`, `mentioned`, null for `user`), `conversation_id` (cascade),
  `segment_id` (set null), `edited`, `deleted_at`, `created_at`, `updated_at`, `search tsvector`.
  Unique (`person_id`, `fingerprint`).
- `tasks` gains `person_id uuid null references people on delete set null`.
- `voice_groups`: `id`, `model`, `centroid bytea`, `count`, `skipped_until`, `created_at`,
  `updated_at`. `segment_fingerprints` gains `group_id` (set null) and `grouped boolean`.
- `person_voiceprints`: `person_id` primary key (cascade), `model`, `centroid bytea`, `count`,
  `updated_at`.
- `voice_matches`: `id`, `conversation_id` (cascade), `person_id` (cascade), `segment_ids bigint[]`,
  `similarity real` (the best), `status`, `skipped_until`, `created_at`, `decided_at`. Unique
  (`conversation_id`, `person_id`).
- `calendar_events`: (`uid`, `starts_at`) key, `ends_at`, `title`, `attendees text[]`, `fetched_at`.
- `briefs`: `id`, `event_uid`, `event_starts_at` (unique together), `person_ids uuid[]`, `text`,
  `created_at`.

New data leaving the server: event titles and attendee names go to the language model in a brief
request; facts and briefs go to webhooks. README's "What it stores" and "Configuration and
security" say so, and that the calendar URL usually contains a token.

## Settings

| Key | Variable | Default | Meaning |
|---|---|---|---|
| `people.suggestNames` | `Nytka__People__SuggestNames` | `true` | Layer 1 |
| `people.facts` | `Nytka__People__Facts` | `true` | Layer 3 extraction |
| `people.voiceMatching` | `Nytka__People__VoiceMatching` | `false` | Layer 2 |
| `people.voiceThreshold` | `Nytka__People__VoiceThreshold` | `0.7` | Similarity, 0.5 to 0.95, for joining a group or matching a voiceprint |
| `calendar.icsUrl` | `Nytka__Calendar__IcsUrl` | empty | ICS feed; environment only (a `Secret` setting), because it usually carries a token |
| `calendar.briefMinutes` | `Nytka__Calendar__BriefMinutes` | `30` | How long before a meeting its brief is made, 5 to 240 |

`0.7` sits above every different-voice score in the Your voice spike (0.01 to 0.63) and below its
same-voice scores (0.84 to 0.95). Those were synthetic voices; real other-versus-other numbers are
UNKNOWN until the evaluation runs.

## API

| Call | Scope | Effect |
|---|---|---|
| `GET /api/v1/people/{id}` | read | `{ id, name, note, createdAt, lastSeenAt, voices, hasVoiceprint, voiceprintSamples, conversations: [{ id, title, startedAt }] (newest 10), facts (newest 50), openTasks }` |
| `PATCH /api/v1/people/{id}` `{ name?, note? }` | admin | as v0.6, plus the note; `note: null` clears it |
| `DELETE /api/v1/people/{id}` | admin | as v0.6; now also voiceprint, facts, links, suggestions, matches |
| `GET /api/v1/people/{id}/facts?before&limit` | read | `{ items: [Fact], nextBefore }`; Fact is `{ id, personId, text, source, basis, conversationId, conversationTitle, segmentId, createdAt, updatedAt }` |
| `POST /api/v1/people/{id}/facts` `{ text }` | admin | 201 with the fact (`source: user`); 409 when a live fact holds it |
| `PATCH`, `DELETE /api/v1/people/{id}/facts/{factId}` | admin | edit (200) or tombstone (204) |
| `GET /api/v1/people/suggestions?status=pending` | read | `{ items: [{ id, conversationId, target, speakerId, groupId, name, personId, confidence, evidence: { segmentId, startedAt, text } }] }` |
| `POST /api/v1/people/suggestions/{id}/accept`, `/reject` | admin | 200 with the person, or 204; 409 when no longer pending |
| `GET /api/v1/people/cards` | admin | `{ items: [{ kind: group\|match, id, conversationId, conversationTitle, personId, personName, similarity, clip: { from, until }, lines: [{ segmentId, startedAt, text }] }] }`, at most 4; empty when voice matching is off |
| `GET /api/v1/people/cards/{kind}/{id}/clip` | admin | `audio/ogg`, at most 10 s; 404 when the audio is gone |
| `POST /api/v1/people/cards/{kind}/{id}` `{ personId } \| { name } \| { skip: true } \| { reject: true }` | admin | name or confirm (200 with the person), skip for 7 days or reject (204) |
| `DELETE /api/v1/people/voiceprints` | admin | every group, person voiceprint and pending match. 204 |
| `GET /api/v1/people/voice-eval?since&until&limit` | admin | For the evaluation: `{ items: [{ segmentId, conversationId, durationMs, groupId, personId, matchPersonId, similarity }], nextSince }`, no text, no vector |
| `PATCH /api/v1/segments/{id}` `{ isUser?, personId? }` | admin | `personId` sets or clears (null) the segment's person |
| `PATCH /api/v1/tasks/{id}` | admin | also `personId`; Task gains `personId` and `personName` |
| `GET /api/v1/search` | read | `kinds` gains `person`; a hit's `id` is the person, `snippet` the matching name or fact |
| `GET /api/v1/briefs/upcoming?minutes=` | read | `minutes` 1 to 1440, default 60. `{ items: [{ uid, title, startsAt, endsAt, attendees: [{ name, personId }], brief: { id, text, createdAt } \| null }] }` |
| `GET /api/v1/review?limit=` | read | `{ items: [{ kind: name\|voice\|label, id, conversationId, conversationTitle, at, text, proposal: { name, personId, confidence, similarity, isUser } }] }`, newest first, `limit` default 50 |
| `POST /api/v1/review/{kind}/{id}/accept`, `/reject` | admin | as the routes above for `name` and `voice`; for `label`, stores the wearer's mark |

**MCP.** `list_people` (`{ items: [{ id, name, lastSeenAt, facts }] }`) and `get_person` (`id` or
`name`; the person page without voiceprint fields). `search` takes `person` in `kinds`.

**Webhooks.** `person.fact.created`: `{ id, personId, personName, text, basis, conversationId }`.
`brief.ready`: `{ id, title, startsAt, people: [{ id, name }], text }`. No transcript in either.

**Export.** `person` records gain `note` and `voiceprint: false|true`; new `person_fact` records;
tasks gain `personId`. Groups, voiceprints, suggestions, matches, calendar events and briefs are not
exported.

## How we measure it

Real voices never enter this repository (invariant 6). The owner runs these on their own server and
keeps labels and results outside it.

**Layer 2, other people's voices.** How well TitaNet-small tells two *other* people apart, in
Ukrainian and Russian, is UNKNOWN: the only evidence is the Your voice spike, where two synthetic
female voices (Ukrainian and Russian) scored 0.58 to 0.63 against each other.

1. **Sample.** At least 300 fingerprinted non-wearer segments from at least 10 conversations, with
   at least 4 people you know, each speaking in at least 2 conversations; Ukrainian and Russian both
   present, plus one noisy place.
2. **Label blind.** Write the true person (or `unknown`, `mixed`) per segment id in a CSV, by
   listening in the app, without looking at groups.
3. **Join** with `GET /api/v1/people/voice-eval` for the same span.
4. **Report, by language and by duration (1 to 2 s, 2 to 4 s, 4 s and more):** group purity (share
   of a group's segments from its majority person), fragmentation (groups per person), false match
   (share of matched segments whose voiceprint person is wrong), and the same for thresholds from
   0.5 to 0.9 in steps of 0.02.
5. **Pass**, for segments of 2 s or longer, in each language: false match at most 2% and purity at
   least 95% at the chosen threshold. Otherwise `people.voiceMatching` stays off by default and the
   README says what was measured.

**Layer 1, names from text.** Accept or reject the first 50 suggestions on real conversations.
Pass: at least 90% accepted. Below that, raise the confidence floor or narrow the prompt.

In the repository, tests use synthetic tones and a fake `ISpeakerEmbedder` returning set vectors,
and a hand-written `ILlmClient`.

## Conflicts with the code

1. **Grouping needs the wearer enrolled and retention of a day or more.** Both follow from today's
   code (see What exists today). The spec keeps "no voiceprint, no fingerprints"; dropping it would
   mean fingerprinting everyone, the wearer included, before anyone enrolled.
2. **"SSRF-safe like webhooks."** Webhooks allow private and loopback addresses on purpose. The
   calendar fetch follows the webhook rules (no redirects, timeout) plus a size cap, and allows
   private addresses too, since only an admin sets the URL. If the owner wants private addresses
   blocked, that is a new rule with no precedent in the code.
3. **`calendar.icsUrl` is environment-only.** A private ICS URL is a credential; invariant 8 keeps
   credentials out of the table and responses, so the app cannot edit it. Making it editable needs a
   new kind of setting.
4. **Tasks "owed to or from".** Tasks today exclude what others promised the wearer, so only "to"
   exists. Tracking promises made to the wearer means changing the task rules of v0.6, which this
   spec does not do.
5. **The label rule lives in three places**, not one (`SpeakerLabel`, `ConversationStore`,
   `ExportStore`). The first task folds the two copies into `SpeakerLabel.Joins`.
6. **`nytka_search_setup()`** (migration 0007) clears vectors of three named tables. Adding people
   and facts to search means a new migration that replaces the function with all five.

## Out of scope

- Recognising people by voice without the wearer enrolled, or on segments under 1 s or over 30 s.
- Voiceprints of people never confirmed, kept beyond their audio.
- Grouping or matching segments transcribed before voice matching was turned on (their audio may
  still exist, but no fingerprints do).
- Promises other people made to the wearer as tasks.
- Calendar write access, CalDAV, OAuth calendars, attendee matching by e-mail.
- Facts in Ask or the daily digest (both read memories today).
- Merging people automatically. The v0.6 merge route stays manual; it now also moves facts (a
  duplicate fingerprint keeps the target's row), segment and task links, and blends the two
  voiceprints when both exist with the same model.
- A consent prompt on the pendant or any change to capture when entering a new place.

## Review focus

1. **Nothing applies itself.** Every path from a model or a similarity to a name goes through an
   accept route. Grep for writes to `person_voices`, `segments.person_id` and `tasks.person_id`.
2. **Biometrics stay put.** Only Postgres holds a vector; groups die with their last fingerprint;
   forgetting a person removes their voiceprint in the same transaction.
3. **A fact needs a confirmed speaker or a named mention**, decided by the server from the evidence
   segment, never by the model's word.
4. **Transcription never waits.** Grouping is its own job; it never runs inside `transcribe`.
5. **No secret in a log.** The calendar URL, like an API key, never reaches a log or a response.
