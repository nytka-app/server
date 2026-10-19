# Nytka, promise ledger: what I owe a person and what they owe me

The pieces exist: the wearer's commitments are tasks linked to a person (`docs/specs/task-kinds.md`), and what another
person promised the wearer has been kept since `0027_waiting_on.sql` as a task of kind `waiting_on`, linked to the person
who owes it. The person page shows only the open ones, newest 100, and no route answers "what is still open between me and
Anna, and what did we close". This milestone adds that read, and nothing else.

## Decision: no new rows

The first idea was to store other people's promises as rows with owner `other`. That is what `waiting_on` already is: the
classifier turns an item of kind `commitment` and owner `other` into a `waiting_on` task, and an unknown owner stays dropped.
A second representation would be two ways to say one thing, so the ledger reads what is there. No migration.

| Side | Rows | Meaning |
|---|---|---|
| `owedByMe` | tasks of kind `commitment` with the person's id | the wearer will do it for or with that person |
| `owedToMe` | tasks of kind `waiting_on` with the person's id | that person said they will do it for the wearer |

A promise nobody clearly made stays noise: ideas, advice, noise, and items whose owner is unknown never reach the ledger,
as before. A promise of the person that names no known person is not in anyone's ledger; it stays in `GET /api/v1/tasks?kind=waiting_on`.

## Done when

1. `GET /api/v1/people/{id}/ledger` (read scope) answers `{ personId, personName, owedByMe: { open, done }, owedToMe: { open, done } }`.
   Each entry is a task as `GET /api/v1/tasks` shows it, so it carries `conversationId`, `conversationTitle` and
   `conversationStartedAt` (when it was said), `createdAt` and `doneAt`. A task made by hand
   (`POST /api/v1/tasks`) may have no conversation; its conversation fields are null. `404` for an unknown person.
2. `open` lists newest first, at most 100. `done` lists most recently done first, at most 100. Deleted tasks never show.
3. The MCP tool `get_ledger` takes `id` or `name` (one of the two, like `get_person`) and returns the same body; a tool error
   ("No such person.") for an unknown one.
4. `GET /api/v1/info` lists `ledger` under `features`.
5. The export already carries every kind with its `kind` and `personId`, so the ledger is in it; nothing changes there, and a
   test pins it.
6. `waiting_on` rows stay out of the default task list, the conversation detail, the digest, briefs, Ask and the
   `task.created` webhook: only the wearer's commitments appear there, as today. A test pins that the route does not change them.
7. A task the user ticked, edited, re-linked or deleted is never overwritten by a later summary, as before: the ledger reads
   rows and writes none.

## Not in this milestone

- No write route: tick, edit and re-link a row with `PATCH /api/v1/tasks/{id}` as today.
- No "owed to nobody" view and no app screen.
- No ledger for a promise whose person the model could not name.
