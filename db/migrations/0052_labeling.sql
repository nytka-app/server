set local lock_timeout = '5s';

-- The labelling app (docs/explanation/features.md, Labelling): the owner says what kind of audio a clip is. Labels are
-- the record of truth; the marks they set on segments are a derived copy (label_marks says which label set which).

-- A token of scope labeling reaches only /api/v1/labeling/*.
alter table api_tokens drop constraint api_tokens_scope_check;
alter table api_tokens add constraint api_tokens_scope_check check (scope in ('admin', 'read', 'labeling'));

-- A clip the server asks about: one line or a stretch of lines. rank orders the open items of a reason (lowest first);
-- peaks is 100 numbers from 0 to 1 for the waveform, null when the audio was gone. A lease holds an item for one
-- GET /next session (lease_task is the task asked, 'mixed' included).
create table labeling_items (
    id            bigint      generated always as identity primary key,
    task          text        not null check (task in ('device', 'voice', 'quiet', 'transcript', 'overlap')),
    conversation_id uuid      not null references conversations (id) on delete cascade,
    segment_ids   bigint[]    not null,
    started_at    timestamptz not null,
    ended_at      timestamptz not null,
    reason        text        not null check (reason in ('uncertain', 'random', 'disagree', 'repeat', 'top')),
    rank          real        not null,
    guess_model   text        null,
    guess_value   text        null,
    guess_score   real        null,
    peaks         real[]      null,
    speaker_key   text        null,
    lease_task    text        null,
    lease_session uuid        null,
    lease_order   int         null,
    leased_until  timestamptz null,
    answered_at   timestamptz null,
    created_at    timestamptz not null
);

create index labeling_items_open on labeling_items (task, reason, rank) where answered_at is null;
create unique index labeling_items_once on labeling_items (task, segment_ids) where answered_at is null;
create index labeling_items_conversation on labeling_items (conversation_id);
create index labeling_items_segments on labeling_items using gin (segment_ids);

-- Append-only: a new answer on an item supersedes the old label instead of changing it. item_id and task are null for an
-- import; provenance is 'app' or 'import:<name>'. client_id makes a retried answer a no-op. confirmed_at: the owner let
-- this 'me' label teach the voiceprint.
create table labels (
    id               bigint      generated always as identity primary key,
    item_id          bigint      null references labeling_items (id) on delete set null,
    task             text        null,
    conversation_id  uuid        not null references conversations (id) on delete cascade,
    segment_ids      bigint[]    not null,
    started_at       timestamptz not null,
    ended_at         timestamptz not null,
    source           text[]      null,
    device_type      text        null,
    content          text        null,
    transcript       text        null,
    flags            text[]      not null default '{}',
    language         text        null,
    person_id        uuid        null references people (id) on delete set null,
    correction       text        null,
    overlap          text        null,
    lines_differ     boolean     not null default false,
    listened_ms      int         null,
    answered_with    text        null,
    taxonomy_version smallint    not null,
    provenance       text        not null,
    client_id        uuid        null unique,
    confirmed_at     timestamptz null,
    created_at       timestamptz not null,
    superseded_by    bigint      null references labels (id) on delete set null
);

create index labels_item on labels (item_id) where superseded_by is null;
create index labels_created on labels (created_at, id);
create index labels_conversation on labels (conversation_id);

-- The audio of a label, kept past audio.retentionDays only while labeling.keepClips is on; embedding (CLAP, 512 numbers)
-- is not playable and stays either way.
create table label_clips (
    label_id   bigint      primary key references labels (id) on delete cascade,
    body       bytea       null,
    embedding  real[]      null,
    created_at timestamptz not null
);

-- Which label set a segment's mark, and to what: mark 'voice' is is_user_manual ('true' or 'false'), 'speech' is
-- speech_manual. Deleting or superseding the label clears the mark while it still holds that value.
create table label_marks (
    segment_id bigint not null references segments (id) on delete cascade,
    mark       text   not null check (mark in ('voice', 'speech')),
    label_id   bigint not null references labels (id) on delete cascade,
    value      text   not null,
    primary key (segment_id, mark)
);

create index label_marks_label on label_marks (label_id);
