# Nytka, task kinds: a task list that holds only commitments

On 2026-10-06 the owner's open task list had 30 entries on top. At least nine were tips from one table-tennis
lesson ("Maintain a relaxed grip while playing"), and others were noise ("Make this stuff tomorrow",
"Consider the practical applications of the detection system"). The summary prompt already said a task is a
commitment, but one model call that returns "tasks" is asked to find things to do, so it finds them.

This milestone makes the model say what each candidate is before the server decides what to keep.

| Kind | Meaning | Kept as |
|---|---|---|
| `commitment` | the wearer said they will do it, or was asked and did not turn it down | a task |
| `idea` | floated, and nobody took it on | a row of `tasks` with kind `idea` |
| `advice` | a tip, rule or lesson on how to do something well | a point of a note, one note per topic |
| `noise` | a remark, a feeling, a line with no clear action, anything from media | nothing; a row in `dropped_candidates` |

Every item also has an **owner**, `wearer` or `other`. Only the wearer's items are kept: what someone else said they
will do is not the wearer's task, whatever its kind. An item that names a person the user already named in the
conversation links to them, as a task does today.

## Done when

1. The `enrich-conversation` answer carries `items`, each `{ text, kind, owner, person, topic }` with `kind` one of
   the four above and `owner` one of two (a strict schema with enums; `LlmJson` reads it strictly). `tasks` is no longer
   a key of the answer.
2. After a summary, the conversation's tasks are the wearer's commitments (kind `commitment`) and ideas (kind `idea`).
   An unknown kind counts as noise and an unknown owner as `other`, so a model that strays costs a task and never adds one.
3. Advice of the wearer is grouped by topic into one row of `notes` per conversation and topic; a topic is normalized as a
   tag name, and advice with none goes to `general`.
4. Noise and every item owned by someone else are listed in `dropped_candidates` with their kind and owner; the latest
   summary of a conversation replaces its rows.
5. `GET /api/v1/tasks` and MCP `list_tasks` return commitments by default and take `kind` (`commitment`, `idea`, `all`);
   a task carries `kind`. A conversation's `tasks`, a person's open tasks, the digest, the webhook payloads, briefs and Ask read
   commitments only. The export carries every kind, each task with its `kind`.
6. `GET /api/v1/notes` and MCP `list_notes` list notes. `GET /api/v1/info` lists `task-kinds` under `features`.
7. `task.created` is published for a new commitment, and for an idea a later summary promotes to one; never for an idea.
8. A task the user ticked, edited or deleted keeps its kind and is never removed by a later summary, as before.

## Why one call, not two

The model already reads the whole conversation once per window to write the title, the summary and the tasks. A second
call to filter the tasks would read the same transcript again (twice the cost and a second failure point), or would
judge the task strings without the lines around them, which is where "who said it" lives. Asking for the kind and the
owner next to each candidate, text first and labels after it, gives the same reasoning in the call that already has the
context. The server still has the last word: it enforces the contract (kinds, owners, caps, duplicates) in
`ItemClassifier`, a pure function with tests, so a model that breaks the rules cannot put noise on the list.

## Data

Migration `0026_task_kinds.sql`:

- `tasks.kind text not null default 'commitment'` (`commitment` or `idea`): every earlier row is a commitment.
- `notes (id, conversation_id, topic, points text[], created_at, updated_at)`, unique per conversation and topic, deleted
  with the conversation.
- `dropped_candidates (id, conversation_id, kind, owner, text, created_at)`, deleted with the conversation.

## How we measure it

`tests/Nytka.Server.Tests/Ai/TaskKindFixture.cs` holds 18 labelled candidates: nine table-tennis tips, four noise lines,
three of the wearer's commitments, one of someone else's and one idea. The tests check that a classifier that honours the
labels leaves exactly the commitments as tasks (precision and recall 1.0 over commitments) and that taking every candidate
as a task, which is what the old prompt let through, does not.

On the owner's server, the audit shows the noise rate of the prompt itself:

```sql
select kind, owner, count(*) from dropped_candidates group by 1, 2 order by 3 desc;
select text from dropped_candidates where kind = 'noise' order by id desc limit 50;
```

The prompt is good enough when, over a week, the owner deletes almost no task by hand and the dropped list holds no
commitment the owner meant. A dropped commitment is a lost task: read the list for the first weeks.

## Not in this milestone

- No rewording of the old tasks: they are relabelled only when their conversation is summarized again.
- No cross-conversation note per topic: a note belongs to one conversation.
- No edit or delete of a note or an idea in the API, and no app screen for notes yet.
- Commitments of other people ("waiting on") are audited, not listed.
