-- Nytka starts from an empty database. Refuse to run on top of the omi-platform warehouse.
do $$
begin
    if exists (select 1 from information_schema.tables
               where table_schema = 'public' and table_name = 'omi_raw') then
        raise exception 'This database belongs to omi-platform. Point Nytka at an empty database.';
    end if;
end $$;

create table capture_sessions (
    id                   uuid primary key,
    started_at           timestamptz not null,
    last_received_at     timestamptz not null,
    processed_through_at timestamptz null
);

-- One row per uploaded chunk. body turns null once its audio is processed; the row stays so a
-- retried upload is recognised as a duplicate.
create table audio_chunks (
    session_id   uuid        not null references capture_sessions (id) on delete cascade,
    first_seq    bigint      not null,
    frame_count  int         not null check (frame_count between 1 and 1500),
    base_time    timestamptz not null,
    last_time    timestamptz not null,
    body         bytea       null,
    received_at  timestamptz not null,
    processed_at timestamptz null,
    primary key (session_id, first_seq)
);

create index audio_chunks_pending on audio_chunks (session_id, first_seq) where body is not null;

create table conversations (
    id         uuid primary key,
    started_at timestamptz not null,
    ended_at   timestamptz not null,
    status     text        not null check (status in ('open', 'closed')),
    created_at timestamptz not null,
    updated_at timestamptz not null
);

create index conversations_started_at on conversations (started_at desc);
create index conversations_ended_at on conversations (ended_at desc);

create table transcription_batches (
    id              bigint generated always as identity primary key,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    started_at      timestamptz not null,
    ended_at        timestamptz not null,
    status          text        not null check (status in ('pending', 'done', 'failed')),
    error           text        null,
    wav             bytea       null,
    offset_map      jsonb       not null,
    response        jsonb       null,
    created_at      timestamptz not null
);

create index transcription_batches_conversation on transcription_batches (conversation_id, started_at);

create table segments (
    id              bigint generated always as identity primary key,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    batch_id        bigint      not null references transcription_batches (id) on delete cascade,
    started_at      timestamptz not null,
    ended_at        timestamptz not null,
    text            text        not null
);

create index segments_conversation on segments (conversation_id, started_at);
create index segments_batch on segments (batch_id);

-- The Opus frames that cover a batch's speech, in the chunk format (renumbered from 0).
create table speech_audio (
    id              bigint generated always as identity primary key,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    batch_id        bigint      not null references transcription_batches (id) on delete cascade,
    started_at      timestamptz not null,
    ended_at        timestamptz not null,
    body            bytea       not null
);

create index speech_audio_ended_at on speech_audio (ended_at);
create index speech_audio_conversation on speech_audio (conversation_id);
create index speech_audio_batch on speech_audio (batch_id);

create table jobs (
    id           bigint generated always as identity primary key,
    kind         text        not null,
    payload      jsonb       not null,
    dedupe_key   text        null,
    run_after    timestamptz not null,
    attempts     int         not null default 0,
    locked_until timestamptz null,
    last_error   text        null,
    created_at   timestamptz not null
);

create unique index jobs_dedupe_key on jobs (dedupe_key) where dedupe_key is not null;
create index jobs_due on jobs (run_after, id);
