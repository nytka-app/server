set local lock_timeout = '5s';

-- The person a task is owed to, or who asked for it (docs/specs/people.md, "Commitments per person").
-- Set when the task is created, from a person the summary named; deleting the person clears it.
alter table tasks
    add column person_id uuid null references people (id) on delete set null;

create index tasks_person on tasks (person_id) where person_id is not null;
