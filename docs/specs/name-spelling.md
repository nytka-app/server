# Nytka, name spelling: a corrected name sticks

When you fix how a person's name is spelled, the model's earlier writing is corrected and the next
transcription and summary spell it right. Today a rename (`PATCH /api/v1/people/{id}`) changes only
the `people` row, so titles, summaries and tasks keep the old spelling.

## What exists today

Verified against `main` at `77aaf3a`:

- Names are `people.name`, unique on `lower(name)`. There are no aliases. Naming a voice with a name a
  person already has uses that person (`POST /api/v1/people`, accepting a suggestion), so it changes
  no spelling. A spelling changes only through `PATCH /api/v1/people/{id}` with `name`, or through
  `POST /api/v1/people/{id}/merge` (the merged person's name disappears).
- Transcription already gets every named person's name: `TranscriptionClient.BuildVocabularyAsync`
  adds them to `stt.vocabulary` in the `prompt` field on every request, read from the table at
  that moment.
- Enrichment already lists the conversation's confirmed speakers in the user message
  (`ConversationPrompt.PeopleLine`), read at run time.
- The model writes `conversations.ai_title`, `conversations.ai_summary` and `tasks.text`. The user's
  own title is `conversations.title`; a hand-edited task has `tasks.edited`; the summary cannot be
  edited. `segments.text` is the provider's transcript.

## Done when

1. Renaming a person that had a name (`named`) from `old` to `new`, and merging such a person into
   another (`old` = the merged person's name, `new` = the target's), replaces `old` with `new` in
   `ai_title` and `ai_summary` of the person's conversations and in `text` of their open tasks, in
   the transaction that renames or merges. A person's conversations are those with a segment by the
   label rule's person (`SpeakerLabel.PersonId`); their tasks are those in those conversations and
   those with `person_id` set to them.
2. A match is the whole `old` string on word boundaries, ignoring case, where a boundary is any
   character that is not a letter or digit. `Anna` does not match `Annabel`. The replacement
   is `new` as typed, except that an all-lowercase or all-capital match keeps its shape
   (`АННА` becomes `ОЛЕНА`).
3. Never changed: `conversations.title`, a task with `edited`, a done or deleted task, a task
   `fingerprint`, `segments.text`, memories, digests, person facts, and conversations of other
   people. A person known only by a role (`named` false) is not respelled: the role word is a
   common noun.
4. The edit is idempotent: running it again changes nothing, because no `old` is left; a rename
   whose names differ only in case respells nothing but the case, once.
5. It is one string replacement per row: no model call, no job, no event. Rows without a match
   are not written. The search vector of a changed row is cleared by the existing trigger and indexed
   again.
6. Later runs spell the new name: the transcription hint and the enrichment people line already read
   `people.name` (no change). A later enrichment replaces the title, summary and tasks as before.
7. Not handled, on purpose: Ukrainian and Russian case endings (`Олену`, `Олені` stay when `Олена`
   becomes `Оленка`), diminutives, a name written in another script, and a surname or first name
   alone when the old name had two words. The match for a one-word name that only changed in its
   ending is a known gap; renaming `Анна` to `Ганна` does not fix `Анні`.
8. README: the `PATCH /api/v1/people/{id}` row of the API table mentions the re-spelling, which a
   merge also does.

## Not in this change

Aliases (a list of past spellings per person), re-spelling transcripts, and an endpoint to run it by
hand. Each is cheap to add later; none is needed for the case in the title.
