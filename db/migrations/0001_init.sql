-- Landing zone, ingest bookkeeping, and the typed projections.
-- Plain Postgres, not TimescaleDB: conversations and memories are discrete records, not
-- fine-grained time series, so there is no hypertable to create here.

-- ---------------------------------------------------------------------------
-- omi_raw: the source of truth. Every table below is a projection of this and
-- must be rebuildable from it without re-calling the API.
-- ---------------------------------------------------------------------------
create table if not exists omi_raw (
    doc_type   text        not null,   -- 'conversation' | 'memory'
    doc_id     text        not null,
    day        date,                   -- conversation: started_at's day; memory: created_at's day
    payload    jsonb       not null,
    spec_ver   text        not null,   -- which understanding of the API shape this was parsed under
    fetched_at timestamptz not null default now(),
    primary key (doc_type, doc_id)
);

create index if not exists omi_raw_doc_type_day_idx on omi_raw (doc_type, day desc) where day is not null;
create index if not exists omi_raw_fetched_at_idx on omi_raw (fetched_at desc);

-- ---------------------------------------------------------------------------
-- Ingest bookkeeping — conversations only. A window is recorded only after every document inside
-- it has been written, so a backfill killed mid-window redoes that window and nothing else.
-- Memories have no date filter on the list endpoint, so they are fully re-listed every cycle
-- instead of windowed; see CLAUDE.md.
-- ---------------------------------------------------------------------------
create table if not exists ingest_window (
    doc_type       text        not null,
    window_start   date        not null,
    window_end     date        not null,
    document_count int         not null,
    completed_at   timestamptz not null default now(),
    primary key (doc_type, window_start)
);

-- ---------------------------------------------------------------------------
-- Projections
-- ---------------------------------------------------------------------------

create table if not exists conversations (
    id                   text primary key,
    created_at           timestamptz not null,
    started_at           timestamptz,
    finished_at          timestamptz,
    day                  date,               -- started_at's calendar day; null if started_at is null
    language             text,
    source               text,
    folder_id            text,
    folder_name          text,
    title                text not null,
    overview             text not null,
    category             text not null,
    emoji                text,
    transcript_text      text,               -- transcript_segments' text, concatenated, for search
    transcript_segments  jsonb,              -- raw array; null unless include_transcript was set
    events               jsonb not null default '[]'::jsonb,
    geolocation          jsonb,
    updated_at           timestamptz not null default now()
);

create index if not exists conversations_day_idx on conversations (day desc);
create index if not exists conversations_category_idx on conversations (category);
create index if not exists conversations_transcript_fts_idx on conversations
    using gin (to_tsvector('english', coalesce(title, '') || ' ' || coalesce(overview, '') || ' ' || coalesce(transcript_text, '')));

-- One row per action item, fanned out of conversations.structured.action_items. Action items carry
-- no id of their own in Omi's schema, so `idx` (their position in that array) is the key, and a
-- re-projection deletes a conversation's rows before re-inserting them.
create table if not exists action_items (
    conversation_id text        not null references conversations (id) on delete cascade,
    idx             int         not null,
    description     text        not null,
    completed       boolean     not null default false,
    completed_at    timestamptz,
    due_at          timestamptz,
    created_at      timestamptz,
    updated_at      timestamptz,
    primary key (conversation_id, idx)
);

create index if not exists action_items_open_due_idx on action_items (due_at) where not completed;

create table if not exists memories (
    id             text primary key,
    content        text        not null default '',
    category       text        not null default 'interesting',
    tags           jsonb       not null default '[]'::jsonb,
    visibility     text,
    manually_added boolean     not null default false,
    reviewed       boolean     not null default false,
    edited         boolean     not null default false,
    created_at     timestamptz,
    updated_at     timestamptz
);

create index if not exists memories_created_at_idx on memories (created_at desc);
create index if not exists memories_category_idx on memories (category);
