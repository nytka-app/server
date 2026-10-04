set local lock_timeout = '5s';

-- docs/specs/people.md, Layer 3. A short fact about one person. fingerprint is that of the text as
-- created and is never recomputed when the fact is edited; it is unique per person, deleted rows
-- included (deleted_at is a tombstone), so a deleted fact does not come back. basis is set by the
-- server from the evidence segment and is null for a fact added by hand. A fact goes with its person
-- and with the conversation it was taken from; the evidence segment may go before either.
create table person_facts (
    id              uuid        primary key,
    person_id       uuid        not null references people (id) on delete cascade,
    text            text        not null check (length(text) <= 300),
    fingerprint     text        not null,
    source          text        not null check (source in ('ai', 'user')),
    basis           text        null check (basis in ('said', 'about', 'mentioned')),
    conversation_id uuid        null references conversations (id) on delete cascade,
    segment_id      bigint      null references segments (id) on delete set null,
    edited          boolean     not null default false,
    deleted_at      timestamptz null,
    created_at      timestamptz not null,
    updated_at      timestamptz not null,
    unique (person_id, fingerprint),
    check ((source = 'ai') = (basis is not null))
);

create index person_facts_live on person_facts (person_id, id desc) where deleted_at is null;

-- Deleting a conversation cascades here.
create index person_facts_by_conversation on person_facts (conversation_id) where conversation_id is not null;

create index person_facts_by_segment on person_facts (segment_id) where segment_id is not null;
