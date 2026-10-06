set local lock_timeout = '5s';

-- docs/specs/task-kinds.md. A row of tasks is a commitment (a task) or an idea; every row from before this
-- migration is a commitment. Lists show commitments unless asked for ideas.
alter table tasks
    add column kind text not null default 'commitment' check (kind in ('commitment', 'idea'));

-- Advice from a conversation, one note per topic: the tips on one subject are points of one note.
-- topic is a normalized tag-like name. A later summary replaces the conversation's notes.
create table notes (
    id              uuid        primary key,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    topic           text        not null check (length(topic) between 1 and 32),
    points          text[]      not null check (cardinality(points) between 1 and 20),
    created_at      timestamptz not null,
    updated_at      timestamptz not null,
    unique (conversation_id, topic)
);

create index notes_newest on notes (id desc);

-- What the model returned and the server did not keep (noise, and items owned by someone else), for measuring the
-- classifier. The latest summary of a conversation replaces its rows; deleting the conversation deletes them.
create table dropped_candidates (
    id              bigint      generated always as identity primary key,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    kind            text        not null check (kind in ('commitment', 'idea', 'advice', 'noise')),
    owner           text        not null check (owner in ('wearer', 'other')),
    text            text        not null,
    created_at      timestamptz not null
);

create index dropped_candidates_by_conversation on dropped_candidates (conversation_id);
