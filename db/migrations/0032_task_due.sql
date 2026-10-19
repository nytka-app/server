set local lock_timeout = '5s';

-- docs/specs/task-create-due.md. A task may have a due instant (null: nobody gave a deadline), and a task made by hand
-- needs no conversation. unique (conversation_id, fingerprint) stays: nulls never collide in it.
alter table tasks
    add column due_at timestamptz null,
    alter column conversation_id drop not null;
