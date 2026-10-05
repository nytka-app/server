# Speech kind: implementation plan

**Spec:** [`docs/specs/speech-kind.md`](../specs/speech-kind.md); the measurements behind it:
[`docs/research/non-human-audio-report.md`](../research/non-human-audio-report.md). Read both first; this plan only
orders the work and names the files and tests.

**Shape.** Six server tasks, each one PR. Each PR passes `dotnet test` with the speaker model present (CI fetches
it), uses synthetic audio and synthetic text only (invariant 6), a hand-written `ILlmClient` where a model is called,
and changes the README sections of what it touches (endpoints, variables, MCP tools, "What it stores") in the same
PR. Nothing in this plan changes what a people feature produces while `speech.mode` is `shadow` or `off`; every task
that touches a people feature has a test that says so.

## Migrations, allocated up front

| Migration | Task | Needs |
|---|---|---|
| `0024_speech_kind.sql` | S-1 | 0013, 0023 |
| `0025_context_ranges.sql` | S-2 | none |

S-3 adds no migration: S-1's columns hold the guess. A feature that needs a table later takes `0026`.

## Order and parallel work

| Wave | Tasks, each in its own worktree | Starts after |
|---|---|---|
| 1 | S-1, S-2 | spec accepted |
| 2 | S-3, S-4 | S-3: S-1 and S-2; S-4: S-1 |
| 3 | S-5 | S-3 |
| 4 | S-6 | all |

**Shared files** that several tasks append to: `Jobs/JobKinds.cs` (kinds, lane map, keys), `Jobs/Scheduler.cs`
(scans), `Api/InfoEndpoints.cs` (features), `Speech/SpeechSettings.cs`, `tests/.../PostgresFixture.cs` (truncate
list), `tests/.../Settings/SettingsApiTests.cs` (the catalog), `.env.example`, `docker-compose.yml`, `README.md`.
Each task only adds lines there; rebase on `main` before merging and keep both sides of a conflict.

## S-1: kinds, marks and the rule

Migration `0024_speech_kind.sql` as the spec's "Storage" (segments columns, the partial index, `speech_state` with
one row inserted: `applied_mode 'shadow'`, `applied_threshold` the default).

1. `Nytka.Storage/SpeechKinds.cs`: the kind names, `Rule` (the spec's "Which kind wins" as SQL over `s`, given the
   applied mode and threshold), `IsMedia` (`s.speech_kind = 'media'`) and `IsCall`, beside `SpeakerLabel` in
   `PeopleStore.cs`, which stays unchanged.
2. `Nytka.Storage/SpeechStore.cs`: `MarkAsync(segmentId, kind?)` and `MarkConversationAsync(conversationId,
   kind?)` (every line the label rule does not make the wearer's), each re-deriving `speech_kind` in the same
   statement; `ApplyAsync(mode, threshold)` (one `update` of `speech_kind` from marks and guesses, then
   `speech_state`); `NeedsApplyAsync(mode, threshold)`.
3. `Server/Speech/SpeechSettings.cs` (`speech.mode`: `off`, `shadow`, `on`; `speech.mediaThreshold`: 0.5 to 0.99),
   `SpeechExtensions.cs` (`AddNytkaSpeech`, from `Program.cs`), `ApplySpeechHandler` (`apply-speech`, Audio lane,
   dedupe key `apply-speech`) queued by `Scheduler.QueueApplySpeechAsync` when `NeedsApplyAsync`, as
   `QueueRescoreAsync` does.
4. `Api/VoiceEndpoints.cs` (`PATCH /segments/{id}`): `speechKind` (`person`, `media`, `call` or null), alone or with
   `isUser` and `personId`; `Api/ConversationEndpoints.cs`: `POST /conversations/{id}/speech` `{ kind }`.
5. Reads: `ConversationStore`'s segment query and `SegmentRow` gain `speechKind`, `speechGuess`, `speechScore`,
   `speechSignals`, `speechMarked`; list items gain `mediaShare` and the `media=hide|only` filter;
   `ExportStore` segments gain `speechKind` and `speechMarked` (`docs/specs/export.md`); `McpQueries` labels a media
   line `Media` and a call line `{label} (call)`.
6. `/api/v1/info` features gain `speech-kind`.
7. Tests: a mark sets `speech_kind` in every mode and survives `ApplyAsync`; null clears it and the guess (in `on`)
   or nothing (in `shadow`) applies again; the bulk route skips wearer lines; `apply-speech` is queued once when the
   mode changes and not again; a read token gets 403 on both writes; `mediaShare` and the filter on a conversation of
   synthetic segments; the export and MCP carry the kind; nothing about kinds changes `GET /people/suggestions`,
   groups or facts (S-4 does that).
8. README: API rows, the two settings, "What it stores", the feature.

## S-2: context ranges

Migration `0025_context_ranges.sql`.

1. `Nytka.Storage/ContextRangeStore.cs`: insert with `on conflict (id) do nothing`, list by time, overlap query
   (`started_at < @to and ended_at > @from`), delete ended before a cutoff.
2. `Api/ContextEndpoints.cs`: `POST /context/ranges` (admin; at most 500 items; validation of kind, route,
   `endedAt >= startedAt`, at most 12 h, not more than a day in the future; `400` names the field, never echoes a
   value), `GET /context/ranges?since&until` (admin).
3. `Pipeline/RetentionHandler.cs`: delete ranges that ended before the audio cutoff in the same run.
4. `/api/v1/info` features gain `context-ranges`.
5. Tests: a retried batch is skipped by id; a range over 12 h, a reversed range and an unknown kind are `400`; a read
   token gets 403; retention deletes old ranges and keeps recent ones; nothing is logged with a time or id.
6. README: "Context from the phone" (what the app sends, what it never sends), API rows, "What it stores".

## S-3: the guess

Needs the study's model and formula: spec "The guess".

1. `scripts/fetch-audio-tagger.sh` (as `fetch-speaker-model.sh`): downloads `yamnet.onnx` and the class map from the
   pinned revision of `andrelgomes/yamnet-onnx` and `tensorflow/models`, checks SHA-256, puts them in
   `src/Nytka.Audio/Models/`; CI fetches them and sets `NYTKA_REQUIRE_AUDIO_TAGGER=1` as it does for the speaker
   model; tests skip without them locally. README: licence line (Apache-2.0, AudioSet-trained weights; no terms for
   weights found: say so).
2. `Nytka.Audio/Tagging/AudioTagger.cs`: `IAudioTagger.Score(ReadOnlySpan<float> samples)` returns the mean frame
   scores for the three classes (indexes from the class map by name), over the first 10 s; onnxruntime session,
   thread-safe, loaded once by `AudioTaggerModel` (as `SpeakerModel`).
3. `Nytka.Server/Speech/SpeechScorer.cs`: the formula, features and defaults of the spec as one pure static method,
   with `Version = 1`; `Stretches.Find(lines, wearerFlags)` (gap 4 s, cut 10 s); phone prior and call rule over
   `ContextRangeStore.OverlappingAsync`.
4. `Nytka.Server/Speech/ClassifySpeechHandler.cs` (`classify-speech`, Audio lane, dedupe key per conversation) and
   `Scheduler.QueueClassifySpeechAsync` (closed conversations, `speech_version` null or below `Version`, no pending
   batch, `speech.mode` not `off`); it decodes a stretch's audio from `speech_audio` by capture time
   (`ChunkFormat`, `Timeline`), writes `speech_guess`, `speech_score`, `speech_signals`, `speech_version` and then
   calls `SpeechStore.ApplyAsync`-style re-derivation for the conversation, in one transaction.
5. `POST /api/v1/speech/backfill?force=` and `GET /api/v1/speech/eval` (the second is S-5's if S-5 is split).
6. Tests: the formula on fixed inputs (a table of 8 cases including each missing feature); `Stretches.Find` on
   synthetic segments (gap, cut, overlap with the wearer); a fake tagger raising `tv` above the threshold with a
   far-from-wearer stretch gives `media` in `on` and no change in `shadow`; a stretch within 3 s of the wearer stays
   `person`; a conversation with no wearer verdict gets no guess; a phone media range adds the signal; a speaker-route
   call range with a wearer line within 3 s gives `call`; with the model file absent the guess still runs `partial`;
   reclassification after a changed threshold does not reload audio; no log line carries a score, id or time.
7. README: "Speech kind" (the scoring in one paragraph, the model file and licence, the setting).

## S-4: media leaves the people features

1. `VoiceGroupStore.Eligible`: with the applied mode `on` (read from `speech_state` in the same query), also
   `s.speech_kind is distinct from 'media' and s.speech_kind is distinct from 'call' and s.speech_guess is not null`
   for non-wearer lines, so a fingerprint waits for its conversation's guess.
2. `NameTargets.Find` and `NameValidator`'s evidence search skip lines whose kind is `media`; `SuggestNamesHandler`
   leaves them out of the prompt.
3. `FactBasis.Of` returns no basis for a media line; `ExtractPersonFactsHandler` and `ExtractMemoriesHandler` leave
   media lines out of their prompts; `EnrichConversationHandler` and `AskService` label them `Media`, and
   `ConversationPrompt` adds "Take no task from lines labelled Media".
4. Every filter runs in C# after the store has read the lines (spec, Conflicts 1); the `TranscriptSegment` record
   gains the kind.
5. `POST /api/v1/speech/backfill` re-queues enrichment, names, facts and memories of conversations whose kinds changed
   after their last run (`ai_status` back to `none`, `people_runs` and `memory_runs` marked pending).
6. Tests, for each feature: with `shadow`, the same output as without any guess (the fake model receives the same
   prompt text, byte for byte); with `on`, a media line is not a target, not evidence for a neighbour's name, not
   grouped, not a fact's evidence, not in the memories prompt, and labelled `Media` in the enrichment prompt; a
   `call` line is named and used for facts but not grouped; a marked `person` line is used as today.
7. README: "People" (what `on` changes), "Speech kind".

## S-5: review inbox and evaluation

1. `ReviewStore`: kind `speech`: per conversation of the last 14 days, the stretch of consecutive non-wearer lines
   whose guess is `media` or `call` with the score nearest the threshold, unmarked, at most 3 per day, newest first;
   accept stores the guess as the mark on the stretch (`media` for `unsure`), reject stores `person`; the item's
   `proposal` gains `speechKind` (the guess) and `lines` (the stretch's segment ids and times, as the `voice` kind).
2. `GET /api/v1/speech/eval` as the spec (no text, no vector), paged by `startedAt`.
3. Tests: an uncertain stretch appears once, a marked one never; accept and reject mark every line of the stretch;
   `speech/eval` carries no text.
4. README: "Review" (the new kind), API rows.

## S-6: docs

1. `docs/vision.md`: "Speech kind" in the roadmap and the glossary (`media`, `call`).
2. `CLAUDE.md`: the new job kinds and their lanes.
3. README: a final read of "Speech kind", "What it stores", "Configuration and security".

## App (android)

Specified in nytka-app/android `docs/specs/speech-kind.md` and planned in its `docs/plans/2026-10-05-speech-kind.md`
(tasks A-S1 to A-S4, each after the server task whose route it calls).

## Review focus

1. **Shadow changes nothing.** Every S-4 test runs the same fixture in `shadow` and `on`; `shadow` matches `main`.
2. **Person first.** `unsure`, a missing guess and a failed classifier read as `person`; grep writes to
   `speech_kind` and `speech_manual`.
3. **Filters after reading.** No kind condition in the SQL that reads a run's input (`LastSegmentId`).
4. **No text** in context ranges, `speech/eval`, logs or problem titles.
5. **Migrations** keep their numbers.
