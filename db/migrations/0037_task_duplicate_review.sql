set local lock_timeout = '5s';

-- NYTKA-75. One link per conversation and task: a re-run of a summary used to record the same match again.
delete from task_duplicates d
using task_duplicates e
where d.conversation_id = e.conversation_id and d.task_id = e.task_id and d.id > e.id;

drop index task_duplicates_by_conversation;
alter table task_duplicates add constraint task_duplicates_once unique (conversation_id, task_id);

-- A summary's task that may be an open task of another conversation (or one made by hand) but is not surely one: it is
-- created anyway and waits in the review inbox as kind task. Accept deletes task_id and links its conversation to
-- duplicate_of as task_duplicates does; reject keeps both.
create table task_duplicate_suggestions (
    id           uuid        primary key,
    task_id      uuid        not null references tasks (id) on delete cascade,
    duplicate_of uuid        not null references tasks (id) on delete cascade,
    status       text        not null default 'pending' check (status in ('pending', 'accepted', 'rejected')),
    created_at   timestamptz not null,
    decided_at   timestamptz null,
    unique (task_id, duplicate_of)
);

create index task_duplicate_suggestions_pending on task_duplicate_suggestions (created_at desc) where status = 'pending';
create index task_duplicate_suggestions_by_duplicate_of on task_duplicate_suggestions (duplicate_of);

-- unique (conversation_id, fingerprint) never collides for a task made by hand (no conversation), so a retried
-- POST /api/v1/tasks added it twice. Open hand-made tasks are now unique by fingerprint; of any open copies already
-- there the oldest stays and the others become deleted, which keeps their rows.
update tasks t
set deleted_at = now(), updated_at = now()
where t.conversation_id is null and not t.done and t.deleted_at is null
  and exists (select 1 from tasks o
              where o.conversation_id is null and not o.done and o.deleted_at is null
                and o.fingerprint = t.fingerprint and o.id < t.id);

create unique index tasks_open_by_hand on tasks (fingerprint) where conversation_id is null and not done and deleted_at is null;
