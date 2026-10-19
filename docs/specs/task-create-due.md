# Nytka, tasks by hand and due dates

The tasks screen of the app can tick, edit and delete a task, but not add one, and a task has no date. Plane NYTKA-112.
This milestone lets the owner create a task without a conversation and give any task a due date.

A task has an optional **due** instant (`due_at timestamptz`). The API reads and writes it as `due`, ISO 8601 text such as
`2026-10-24T15:00:00Z`; a bare date (`2026-10-24`) is accepted and means that day at 00:00 UTC. A task with no deadline has
`due: null`.

## Done when

1. `POST /api/v1/tasks` (admin token) takes `{ text, due?, conversationId?, personId? }` and returns `201` with the task.
   `text` is 1 to 200 characters. The task has kind `commitment` and is marked edited, so a later summary never removes or
   rewrites it. An unknown conversation or person is a 404; a `due` that is not a date is a 400. Text that already exists
   in the same conversation is a 409.
2. A task need not have a conversation: `tasks.conversation_id` is nullable (migration `0032_task_due.sql`, which also adds
   `tasks.due_at`). Such a task has `conversationId`, `conversationTitle` and `conversationStartedAt` null everywhere a task is
   shown. Deleting a conversation still deletes its tasks.
3. `PATCH /api/v1/tasks/{id}` takes `due` too; `null` clears it. Changing the due date marks the task edited.
4. `GET /api/v1/tasks`, the conversation detail, a person's tasks, the export, MCP `list_tasks`/`create_task`/`complete_task`
   and the `task.created` and `task.completed` webhook payloads carry `due` (null when none). MCP `create_task` takes `due`
   and `conversationId` becomes optional.
5. `task.created` is published for a task created by hand, as for a model-found commitment.
6. Enrichment never invents a deadline. The answer's items carry `due`, a date `YYYY-MM-DD` or null; the prompt says to set it
   only when the speaker named a date or a weekday for that very item. The server keeps it only for a commitment, an idea or
   something awaited, and only when it falls from the conversation's date up to 366 days after it; any other value is null. A
   later summary sets `due` on a task the user has not touched, and never changes the due date of one the user ticked, edited
   or deleted.
7. `GET /api/v1/info` lists `task-create` under `features`.

## Not in this milestone

- No reminders or notifications for a due date, and no sorting or filtering by it.
- No time zone for a bare date: the app shows the date it sent.
