set local lock_timeout = '5s';

-- NYTKA-75. A task's conversation_id is a single not-null column, so a task cannot be linked to a second
-- conversation; when a summary's task matches an open one from an earlier conversation by its fingerprint, no
-- second row is inserted into tasks and the match is recorded here instead.
create table task_duplicates (
    id              bigint      generated always as identity primary key,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    task_id         uuid        not null references tasks (id) on delete cascade,
    created_at      timestamptz not null
);

create index task_duplicates_by_conversation on task_duplicates (conversation_id);
create index task_duplicates_by_task on task_duplicates (task_id);
