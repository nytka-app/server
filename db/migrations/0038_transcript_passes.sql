set local lock_timeout = '5s';

-- NYTKA-133, docs/specs/auto-transcription.md (Storage). A pass sends the kept speech of a conversation to the paid engine
-- once and swaps its text for the draft lines in one transaction. stretches and offset_map hold times and categories, never text.
create table transcript_passes (
    id               bigint      generated always as identity primary key,
    conversation_id  uuid        not null references conversations (id) on delete cascade,
    reason           text        not null check (reason in (
                                     'settled', 'piece', 'opened', 'ask', 'bookmark', 'tag', 'task', 'meeting', 'person', 'correction', 'manual')),
    status           text        not null check (status in ('planned', 'submitted', 'done', 'failed', 'stale', 'waiting')),
    engine           text        null,
    external_id      text        null,
    external_file_id text       null,
    batch_ids        bigint[]    not null,
    stretches        jsonb       not null,
    offset_map       jsonb       not null,
    audio_ms         int         not null,
    error            text        null,
    created_at       timestamptz not null,
    submitted_at     timestamptz null,
    finished_at      timestamptz null
);

create index transcript_passes_conversation on transcript_passes (conversation_id);
create index transcript_passes_active on transcript_passes (status) where status in ('planned', 'submitted', 'waiting');
-- One pass in flight per conversation.
create unique index transcript_passes_in_flight on transcript_passes (conversation_id) where status in ('planned', 'submitted');

alter table conversations
    add column auto_route      text        null check (auto_route in ('eager', 'skip')),
    add column transcript_tier text        not null default 'draft' check (transcript_tier in ('draft', 'final', 'mixed')),
    add column settled_at      timestamptz null;

-- The pass that covered the batch. A pass is deleted with its conversation, and so is the batch, so no foreign key.
alter table transcription_batches add column pass_id bigint null;

-- pass_id null: a draft line. A paid line keeps the batch it falls in, so retention and cascades work as for any line.
alter table segments
    add column pass_id  bigint null,
    add column category text   null check (category in ('wearer', 'person', 'unknown', 'call', 'media', 'noise'));

create index segments_pass on segments (pass_id) where pass_id is not null;

-- The draft lines a swap replaced, kept beside the final text for measurement; they go with the speech audio.
create table segment_drafts (
    id              bigint      primary key,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    pass_id         bigint      not null,
    started_at      timestamptz not null,
    ended_at        timestamptz not null,
    text            text        not null,
    engine          text        null
);

create index segment_drafts_conversation on segment_drafts (conversation_id);
create index segment_drafts_ended_at on segment_drafts (ended_at);

-- A paid pass is not a batch: a ledger row belongs to exactly one of them.
alter table transcription_usage alter column batch_id drop not null;
alter table transcription_usage add column pass_id bigint null;
alter table transcription_usage add constraint transcription_usage_one_source check ((batch_id is null) <> (pass_id is null));
create unique index transcription_usage_pass on transcription_usage (pass_id) where pass_id is not null;
