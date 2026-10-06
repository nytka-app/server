set local lock_timeout = '5s';

-- docs/specs/task-kinds.md, "Waiting on". What another person promised the wearer is kept as a task of kind waiting_on,
-- hidden from the default list. person_id is the person who owes it, when the model named a known one.
alter table tasks drop constraint tasks_kind_check;
alter table tasks
    add constraint tasks_kind_check check (kind in ('commitment', 'idea', 'waiting_on'));

-- The person page reads a person's open waiting-on rows.
create index tasks_waiting_on_by_person on tasks (person_id) where kind = 'waiting_on' and not done and deleted_at is null;
