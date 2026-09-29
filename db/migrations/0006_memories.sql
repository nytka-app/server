-- The foreign keys below take a lock on conversations; give up rather than queue behind a long
-- transaction while the server holds locks on hot tables.
set local lock_timeout = '5s';

-- Lasting facts about the user. fingerprint is that of the text as created or last rewritten by
-- extraction and is never recomputed when the memory is edited; deleted_at is a tombstone, so a
-- deleted fact does not come back. A memory goes with the conversation it was taken from.
create table memories (
    id              uuid        primary key,
    text            text        not null check (length(text) <= 300),
    fingerprint     text        not null unique,
    source          text        not null check (source in ('ai', 'user')),
    conversation_id uuid        null references conversations (id) on delete cascade,
    edited          boolean     not null default false,
    deleted_at      timestamptz null,
    created_at      timestamptz not null,
    updated_at      timestamptz not null
);

create index memories_live on memories (id desc) where deleted_at is null;

-- Deleting a conversation cascades here.
create index memories_by_conversation on memories (conversation_id) where conversation_id is not null;

-- One row per conversation the extraction has looked at. through_segment_id is the highest
-- segments.id it read, so a run repeated over the same speech ends quietly.
create table memory_runs (
    conversation_id    uuid        primary key references conversations (id) on delete cascade,
    status             text        not null check (status in ('pending', 'done', 'failed')),
    through_segment_id bigint      null,
    failures           int         not null default 0,
    message            text        null,
    updated_at         timestamptz not null
);
