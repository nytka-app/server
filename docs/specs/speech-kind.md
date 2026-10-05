# Nytka, speech kind: people in the room, media and calls

The pendant records everything near it, and much of that is not a person talking near the wearer: a TV, a video
on the phone, a podcast, music with vocals, the far side of a call on speaker. Nytka treats every voice as a person,
so a presenter's voice gets a fingerprint and a voice group, a name read out by a video becomes a name suggestion,
and a film's dialogue can become a fact. This milestone gives every line of a transcript a **speech kind**:

| Kind | Meaning |
|---|---|
| `person` | someone physically near the wearer, the wearer included |
| `media` | sound from a device: TV, video, podcast, radio, music, a voice assistant |
| `call` | the far side of a phone or video call heard through a loudspeaker |
| `unsure` | Nytka cannot tell; treated as `person` everywhere |

The research behind it, with every measurement: [research/non-human-audio-report.md](../research/non-human-audio-report.md).

Four rules shape it:

1. **A person called media is worse than media called a person.** A missed person loses data the product
   promises to keep; a leaked media voice is noise the owner can reject. Every threshold leans toward `person`,
   and `unsure` counts as `person`.
2. **Nothing disappears.** A media line stays in the transcript, search, export and MCP, marked as media. The
   owner flips any line, or every other voice of a conversation, with one tap, in every mode.
3. **Shadow first.** By default Nytka computes, stores and shows its guess and changes nothing else. Media stops
   feeding the people features only after the owner turns `speech.mode` to `on`, and the evaluation in "How we
   measure it" says when that is safe.
4. **No new data leaves the server to decide.** The guess uses stored timings, a 16 MB audio model that runs on the
   server's CPU and the phone's own playback times. No audio and no transcript text go to a new recipient; a language
   model is not used (the study's LLM judge called 19 of 91 people media or call).

## Done when

1. A few scheduler ticks after a conversation closes (when its transcription is done and some line has a wearer
   verdict), every line of it has a guess (`person`, `media`, `call` or `unsure`), a score from 0 to 1 and the
   signals that decided it; the wearer's own lines are `person` with the signal `wearer`. A conversation with no
   wearer verdict anywhere gets no guess.
2. With `speech.mode` at `shadow` (the default) or `off`, names, voice groups, cards, matches, facts, tasks,
   memories and tag proposals read every line as they do today.
3. With `on`, a line whose kind is `media` is never a name or role target or evidence, never grouped, matched or
   shown on a card, and never the source of a person fact, task or memory; a `call` line is never grouped, matched
   or carded. Media lines still appear in transcripts, search, export and MCP, each carrying its kind.
4. `PATCH /api/v1/segments/{id}` with `speechKind`, or `POST /api/v1/conversations/{id}/speech` for every other
   voice of a conversation, marks lines; a mark wins over the guess in every mode, survives reclassification, and
   `null` clears it.
5. The app sends context ranges (the phone played media through its own loudspeaker; the phone was in a call) and
   the guess uses them. A range carries a kind, a route and two times: never an app, a title or a number.
6. `GET /api/v1/speech/eval` lists guesses and marks without text, so the owner can run the shadow evaluation.
7. Conversation lists carry `mediaShare`, and `GET /api/v1/conversations?media=hide` leaves out conversations
   whose speech is mostly media.

## What exists today

Verified against `main` at `953b6ce`:

- No classification of any kind. Media appears only as prompt text: `ConversationPrompt.cs:66` (no tasks from it),
  `ExtractMemoriesHandler.cs:152` and `ExtractPersonFactsHandler.cs:233` (no facts from it), and as a documented
  limit in [people.md](people.md) ("Limits": a name spoken by media can be suggested; reject it).
  `NamePrompt` says nothing about media.
- The wearer is decided by one SQL rule, `SpeakerLabel.IsUser` (`Nytka.Storage/PeopleStore.cs:16`).
- Fingerprints are made in `TranscribeHandler` from the batch WAV, which is dropped in the same transaction
  (`BatchStore.CompleteAsync`). Anything computed from audio must be computed there or decoded again from
  `speech_audio` while retention keeps it.
- Grouping, its scheduler gate and the cards share one predicate, `VoiceGroupStore.Eligible`
  (`VoiceGroupStore.cs:64`). Name targets come from `NameSuggestionStore` (`Unnamed`, `:107`) and
  `NameTargets.Find`; a name's evidence may be any of the three lines either side (`NameValidator.cs:149-174`),
  so a media line can name a real voice next to it. Facts take their basis from `FactBasis` (`:46-50`); memories
  read the whole transcript (`MemoryStore.cs:122-129`); enrichment renders every segment
  (`EnrichConversationHandler.cs:84`).
- Re-runs follow the highest segment id read (`LastSegmentId` against `MarkPendingAsync`'s `max(id)`), so a line
  filtered out must be filtered in C# after reading, or every run looks stale; a changed kind changes no id, so
  nothing re-runs by itself.
- The precedent for a stored verdict that follows a setting: `segments.voice_is_user` follows
  `voice_profile.applied_threshold`, and the scheduler queues `rescore-voice` when the two differ
  (`Scheduler.QueueRescoreAsync`, `VoiceStore.NeedsRescoreAsync`).
- The app has no phone-state code at all (no `AudioManager` callbacks, no telephony). Small records reach the
  server through an outbox in Room and an uploader (`BookmarkUploader`, `POST /api/v1/bookmarks` with a client id).
  Review kinds the app does not know are dropped (`ReviewViewModel`).

## The guess

The classifier gives every line a **score**, 0 to 1, for how much it looks like media, and a list of the
**signals** that moved it (fixed codes such as `wearer`, `turn`, `far`, `phone-media`, `phone-call`). The guess
follows from the score and the setting `speech.mediaThreshold` (T):

| Guess | When |
|---|---|
| `person` | the wearer's own line (signal `wearer`, no score), or score below T - 0.15 |
| `call` | a phone `call` range on the loudspeaker covers the line and the voice takes turns with the wearer |
| `media` | score at or above T |
| `unsure` | score from T - 0.15 up to T |

Changing T re-derives every guess from its stored score (`apply-speech`), so no audio or model is needed again.

### What it computes

Per **stretch**: consecutive non-wearer lines of one conversation with gaps under 4 s, cut at 10 s (the unit the
study labelled). Every line of a stretch gets the stretch's score. Features, as the study measured them:

| Feature | Meaning |
|---|---|
| `log_d` | `ln(1 + d)`, `d` = seconds from the stretch to the nearest wearer line in the conversation (0 when they overlap; 3600 when the conversation has none) |
| `log_run` | `ln(1 + r)`, `r` = seconds of non-wearer speech in the run between two wearer lines that holds the stretch |
| `wearer_share` | the wearer's speech time over all speech time of the conversation, 0 to 1 |
| `tv`, `narr`, `synth` | `ln(p + 0.0001)` of YAMNet's mean score over the stretch's first 10 s of audio for `Television`, `Narration, monologue` and `Speech synthesizer` |

The score is the logistic of a fixed linear model (`speech_version` 1), fitted on the owner's 133 labelled clips
(27 media, 106 person) with classes weighted equally, so **it is not a probability of media**; with 3 to 4% media in
the data a score of 0.94 means "no labelled person scored this high", not "94% likely":

`logit = 10.9459 + 0.3790·log_d + 0.0889·log_run − 4.1094·wearer_share + 0.5239·tv + 0.3023·narr + 0.8129·synth`

A missing audio feature (the speech audio is gone, or the model file is absent) takes the study's median (`tv`
−7.642, `narr` −5.525, `synth` −8.266), a missing structure feature `log_d` 3.438, `log_run` 3.542, `wearer_share`
0.46, and the signal `partial` is recorded. A conversation with no line that has a wearer verdict (the first days of
a server, before diarization or enrollment) gets no guess at all: its structure says nothing.

**Phone prior.** A phone `media` range on the loudspeaker that covers at least half of the stretch adds 1.0 to the
logit and the signal `phone-media`. The 1.0 is a provisional prior, not a fit: only a day of phone history
overlapped the labelled clips; shadow data refits it.

**Calls.** A phone `call` range with route `speaker` that covers the stretch, together with a wearer line within 3 s
of it, makes the guess `call` whatever the score; without that wearer line the score decides. Calls are not measured
(0 in 140 labelled clips).

### Measured

Leave-one-conversation-out on the same labels, score threshold at the highest value no labelled person reaches:

| Model | AUC | Media caught with no person called media | With 1 person called media |
|---|---|---|---|
| Structure only | 0.93 | 8 of 27 | 16 of 27 |
| Structure and YAMNet (this model) | 0.94 | **12 of 27** | 13 of 27 |

The default threshold `speech.mediaThreshold` is **0.94**, the out-of-sample "no person" value. Details and the
rejected candidates: [the report](../research/non-human-audio-report.md). The model file is YAMNet (Apache-2.0,
3.7M parameters, 15.7 MB, 16 kHz input, 12 ms per second of audio on one CPU thread in the study's container),
fetched by `scripts/fetch-audio-tagger.sh` from `andrelgomes/yamnet-onnx` at its pinned revision and checked against
SHA-256 `1510041dce24a2e9e84ec546807ac408ae496da6d1ed41bc3ccba649623f8e19`, like the speaker model.

### When

`classify-speech` (Audio lane, dedupe key per conversation) is queued by the scheduler's scan for a closed
conversation whose lines lack `speech_version` 1 (or an older one), after its transcription batches are done. It
reads the conversation's lines, decodes the stretch's audio from `speech_audio`, scores, and writes the guess, score
and signals in one transaction, then applies the kind. Without the model file it still runs with the audio features
missing and the signal `partial`. With `speech.mode` `off` it queues nothing. Audio is kept `audio.retentionDays`
(14); a conversation older than that is guessed from structure alone.

## Which kind wins

For every line, first match wins:

1. The owner's mark (`speech_manual`), in every mode.
2. With `speech.mode` at `on`: the guess, with `unsure` read as `person`. The wearer's own lines are guessed
   `person`, so the wearer is never media unless the owner marks a line so.
3. Otherwise none: the line is treated as today.

The result is stored as `segments.speech_kind` (null when step 3 applies), so every reader asks one column:
`s.speech_kind = 'media'`. `speech_state.applied_mode` and `applied_threshold` record what the stored guesses and
kinds follow; when the settings differ, the scheduler queues `apply-speech`, which re-derives every guess from its
score and every kind from mark and guess in one `update`, as `rescore-voice` does for the wearer. The stored kind
depends on nothing else, so a later change of the wearer's verdict cannot leave it stale; such a change clears the
conversation's `speech_version`, and the scan guesses it again.

## What media stops feeding

With `speech.mode` at `on`:

| Feature | Change | Where |
|---|---|---|
| Name and role suggestions | a media line is neither a target nor evidence; it is left out of the names prompt | `NameSuggestionStore` (`Unnamed`), `NameValidator` evidence search, `SuggestNamesHandler` render |
| Voice groups, matches, cards | `media` and `call` lines are not eligible | `VoiceGroupStore.Eligible` |
| Person facts | a media line is never evidence (no `mentioned` basis either) and is left out of the prompt | `FactBasis.Of`, `ExtractPersonFactsHandler` render |
| Memories | media lines are left out of the input | `ExtractMemoriesHandler` render |
| Tasks, summary, conversation tags | media lines stay, labelled `Media`, so the summary can say a TV was on; the prompt takes no task from `Media` lines | `EnrichConversationHandler` render, `ConversationPrompt` |
| Ask | a media snippet is labelled `Media` in the context | `AskService` render |
| People map | follows from the rows above: no new link from a media line | |

Lines are filtered in C# after the stores read them, never in the reading SQL, so `LastSegmentId` stays the
conversation's highest id (see What exists today). A conversation whose kinds changed is re-run by
`POST /api/v1/speech/backfill`, which re-queues enrichment and the people runs of conversations reclassified since
their last run.

Fingerprints of media lines are not deleted: they die with their audio after `audio.retentionDays`, as today, and
the owner's flip back to `person` can still group them.

## Context from the phone

The app records, with no new permission:

| Range | Opens when | Closes when |
|---|---|---|
| `media` | `AudioManager.registerAudioPlaybackCallback` reports an active player with usage media, game or unknown, while the route for media is the built-in loudspeaker and the music volume is above 0 | no such player for 2 s, or the route leaves the loudspeaker |
| `call` | `AudioManager` mode becomes `IN_CALL`, `IN_COMMUNICATION` or a redirect mode (`addOnModeChangedListener`) | the mode returns to `NORMAL` |

A `call` range carries its route (`speaker`, `earpiece`, `headset`, `bluetooth`) from the communication device;
only a call on `speaker` can be heard by the pendant as a voice. Android does not tell a non-system app which app
plays, so ranges carry no app name. Playback through headphones or Bluetooth is not recorded at all: the pendant
cannot hear it.

Ranges wait in Room (`context_outbox`) and go to `POST /api/v1/context/ranges` in batches, each with a client id
so a retry is harmless, the same way bookmarks do. The app sends them while `/api/v1/info` lists `context-ranges`
and its "Phone context" switch (Device tab, on by default) is on.

## Storage

Migrations, numbers fixed by the plan:

- `0024_speech_kind.sql`: `segments` gains `speech_guess text null` (`person`, `media`, `call`, `unsure`),
  `speech_score real null`, `speech_signals text[] null`, `speech_version smallint null`, `speech_manual text null`
  (`person`, `media`, `call`) and `speech_kind text null` (`person`, `media`, `call`); a partial index on
  `(conversation_id) where speech_kind = 'media'`. `speech_state`: one row, `applied_mode`, `applied_threshold`,
  `updated_at`.
- `0025_context_ranges.sql`: `context_ranges`: `id uuid` primary key (the app's), `kind` (`media`, `call`),
  `route` (`speaker`, `earpiece`, `headset`, `bluetooth`, `other`), `started_at`, `ended_at` (`ended_at >=
  started_at`, at most 12 h), `received_at`; an index on `(started_at)`. Retention deletes ranges that ended before
  the audio retention cutoff, in the same run that deletes speech audio.

`segments` has no feature columns: the signals are the stored explanation (`speech_signals`, fixed codes `wearer`,
`far`, `run`, `share`, `tv`, `narr`, `synth`, `phone-media`, `phone-call`, `partial`, each present when it moved
the logit by more than 0.5 towards media), so the guess can be explained without storing audio features.

## Settings

| Key | Variable | Default | Meaning |
|---|---|---|---|
| `speech.mode` | `Nytka__Speech__Mode` | `shadow` | `off`: no guess; `shadow`: guess and show, change nothing; `on`: media and calls leave the people features as above |
| `speech.mediaThreshold` | `Nytka__Speech__MediaThreshold` | `0.8` | Score at or above which a guess is `media`, 0.5 to 0.99 (provisional; S-3 sets it from the evaluation) |

`speech.mode` and `speech.mediaThreshold` are the only keys. The model's coefficients are code, versioned by
`speech_version`; a new version re-guesses through `POST /api/v1/speech/backfill?force=true`.

## API

| Call | Scope | Effect |
|---|---|---|
| `GET /api/v1/conversations/{id}` | read | each segment gains `speechKind` (the kind that applies, or null), `speechGuess`, `speechScore`, `speechSignals` and `speechMarked` |
| `GET /api/v1/conversations?media=hide\|only` | read | items gain `mediaShare` (share of the conversation's speech time whose kind is `media`); `hide` leaves out items with `mediaShare` of 0.8 or more, `only` keeps them |
| `PATCH /api/v1/segments/{id}` `{ speechKind }` | admin | `person`, `media`, `call` or null (clears the mark); combinable with `isUser` and `personId`; any line, the wearer's included, so a TV voice taken for the wearer can be fixed |
| `POST /api/v1/conversations/{id}/speech` `{ kind }` | admin | marks every line of the conversation that is not the wearer's with `kind`, or clears those marks with null; `200 { marked }` |
| `POST /api/v1/context/ranges` `{ items: [{ id, kind, route, startedAt, endedAt }] }` | admin | up to 500 ranges; an id seen before is skipped; `200 { accepted, skipped }`; `400` names the bad field |
| `GET /api/v1/context/ranges?since&until` | admin | for developer mode: `{ items: [{ id, kind, route, startedAt, endedAt }] }` |
| `GET /api/v1/speech/eval?since&until&limit` | admin | `{ items: [{ segmentId, conversationId, startedAt, durationMs, isUser, guess, score, signals, marked, kind }], nextSince }`, no text |
| `POST /api/v1/speech/backfill?force=` | admin | guesses conversations with unguessed lines (`force=true`: every conversation in retention, or with an older `speech_version`), then re-queues their runs; `{ queued, remaining }` |
| `GET /api/v1/review` | read | kind `speech`: an unmarked stretch of one conversation guessed `media`, `call` or `unsure`, nearest the threshold first, at most 3 a day; accept stores the guess (`media` for `unsure`) as the owner's mark on the stretch, reject stores `person` |

`/api/v1/info` `features` gains `speech-kind` and `context-ranges`.

**MCP.** `get_conversation` labels a media line's speaker `Media` (a call's keeps its label with ` (call)`), so an
agent reading a transcript knows; `list_conversations` items carry `mediaShare`.

**Export.** `segment` records gain `speechKind` and `speechMarked`; the format `version` stays `1`.

**Webhooks.** None: a kind is not a new item.

## The app

- A transcript line whose kind is `media` shows a "Media" chip and muted text; `call` shows "Call". In shadow
  mode a guess shows as an outlined chip with a question mark ("Media?"), so the owner sees what `on` would do.
- Long-press on a line adds "Person here", "Media", "Call" and "Clear" beside "This is me" and "Not me".
- The conversation menu gains "Other voices are media" and "Other voices are people", which call the bulk route.
- The conversation list shows a "Media" chip on items with `mediaShare` of 0.8 or more, and a "Hide media" filter.
- The review inbox shows `speech` items ("Was this a TV or a video?") with the lines, Yes and No.
- Device tab: "Phone context" switch with one sentence on what it sends; developer mode lists the last ranges.
- Room goes to version 6 (`context_outbox`), with an automatic migration and its test.

## Privacy

- Fewer voiceprints of strangers: with `on`, media and call voices never enter a voice group, so no stranger's
  centroid outlives its audio through a group, and no card asks "Who is this?" about a presenter.
- Context ranges say when the owner's phone played sound through its loudspeaker or was in a call. They go only to
  the owner's server, carry no app, title or number, and die with the audio retention. README's "What it stores"
  says so, and the app's switch turns them off.
- No new third-party flow: the study's LLM judge is not shipped, so no transcript goes to a model for this.
- The transcription adapter outside this repository keeps every voice's fingerprint indefinitely, media voices
  included. That is not Nytka's to change; the report recommends a retention policy there.

## How we measure it

Real voices never enter this repository (invariant 6). The owner runs this on their own server and keeps labels
and results outside it.

**The gate before `on`.** Shadow mode is the evaluation. Each day Nytka queues at most 3 `speech` review items (the
stretches whose score is nearest the threshold, above and below) and the owner's taps, plus every mark, are labels.
`GET /api/v1/speech/eval` joins guesses and marks without text. `on` is safe when, over the last 100 stretches
guessed `media` that the owner reviewed or marked:

1. at least 95% are media (5 or fewer people), and none was a stretch that contained the wearer's own speech;
2. the same holds over the last 100 guessed `unsure`, read as person: at least 80% are people.

Below that, raise `speech.mediaThreshold`, or leave the mode at `shadow`. The study's 27 media clips fix the model;
they do not prove the rate in the field, which is why the threshold is high and the first weeks are shadow.

**Re-running when the model changes.** The study's harness lives outside the repository (it handles real audio).
A new model version re-guesses from stored features it can recompute, and the owner re-labels nothing: the marks
the owner already made are the labels, and `speech/eval` is the data. An AUC and the count of people called media
at the threshold, from `speech/eval`, are what to compare between versions.

In the repository, tests use synthetic tones and text, a fake tagger returning set scores, and a hand-written
scoring fixture that checks the formula above against fixed inputs.

## Conflicts with the code

1. **Stores read every line; filters go in C#.** Filtering media in the SQL that reads a conversation would change
   `LastSegmentId` and make every run look stale. Each handler drops media lines after reading.
2. **A changed kind re-runs nothing by itself** (re-runs follow segment ids). Reclassification therefore queues the
   affected runs explicitly through `POST /api/v1/speech/backfill`.
3. **Grouping runs every minute, before a conversation closes.** With `on`, `Eligible` also requires a guess, so a
   fingerprint waits for its conversation's guess instead of joining a group as a stranger first.
4. **`ReviewItem.ConversationId` is required.** A `speech` item is a stretch of one conversation, so it has one.
5. **The app drops unknown review kinds.** An older app simply does not show `speech` items.

## Out of scope

- Deleting or hiding anything automatically; a media conversation is never deleted, merged or skipped.
- Automatic tags: the tags spec keeps tags hand-made; the `media` filter is a query parameter, not a tag.
- Telling which app, show or song played; Android does not tell, and Nytka does not ask.
- Recording the phone's own audio (`AudioPlaybackCaptureConfiguration` needs `RECORD_AUDIO`, which the app never
  holds), Cast or smart-TV state, and new pendant firmware for spatial cues.
- Changing the transcription adapter.

## Review focus

1. **Person first.** Every path that can produce `media` is a guess above the threshold, a context range, or the
   owner's mark; `unsure` and missing data read as `person`. Grep for writes to `speech_kind`.
2. **Shadow changes nothing.** With `shadow`, every people feature produces the same output as on `main`; tests
   compare.
3. **Filters after reading.** No `speech_kind` condition in the SQL that reads a run's input.
4. **No text, title or app name** in context ranges, logs, errors or `speech/eval`.
5. **Migrations** keep their numbers.
