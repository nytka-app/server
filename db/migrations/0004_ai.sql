-- When the batch reached done or failed, by the database clock. Null while it is pending, and for
-- batches from before this migration.
alter table transcription_batches add column finished_at timestamptz null;

-- speaker comes from the transcription answer. created_at is when the transcript was stored, so
-- created_at - ended_at measures the transcript latency; rows from before this migration all carry
-- the migration's time.
alter table segments
    add column speaker    text        null,
    add column created_at timestamptz not null default now();

-- title is the one the user set and wins over ai_title. ai_through_segment_id is the highest
-- segments.id the last run read, so speech that arrives later starts another run.
alter table conversations
    add column title                 text        null,
    add column ai_title              text        null,
    add column ai_summary            text        null,
    add column ai_status             text        not null default 'none'
        check (ai_status in ('none', 'pending', 'done', 'skipped', 'failed')),
    add column ai_message            text        null,
    add column ai_updated_at         timestamptz null,
    add column ai_through_segment_id bigint      null,
    add column ai_failures           int         not null default 0;

-- Tasks come from the summary. fingerprint is that of the AI text and is never recomputed when the
-- task is edited; deleted_at is a tombstone, so a deleted task does not come back.
create table tasks (
    id              uuid        primary key,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    text            text        not null,
    fingerprint     text        not null,
    done            boolean     not null default false,
    done_at         timestamptz null,
    edited          boolean     not null default false,
    deleted_at      timestamptz null,
    created_at      timestamptz not null,
    updated_at      timestamptz not null,
    unique (conversation_id, fingerprint)
);

create index tasks_live on tasks (id desc) where deleted_at is null;
