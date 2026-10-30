set local lock_timeout = '5s';

-- Situations (epic nytka-app/server#296, A8): the unit the app shows and extraction reads, stored instead of cut on every read. The
-- build-situations job cuts a closed day's conversations (scene cuts, then a cut where the set of present speaker tracks changes),
-- and a situation lies inside one conversation. id is a hash of the conversation and the start, so a rebuild finds its rows again;
-- version counts the changes of a situation's content (span, tracks, lines), and lines_hash is the hash of its lines alone, so a
-- change of tracks never costs a new summary. track_ids are the tracks present (no foreign key: a rebuild of the day's tracks
-- may replace them; the readers name people from the lines). place is null until the server knows places. activity is
-- conversation, media (media_share at 0.8 or more) or noise (a fragment under 60 s with no speech and no neighbour to join).
-- ai_* follow the conversation's own: ai_lines_hash is the lines_hash the title and summary were made from.
create table situations (
    id              uuid        primary key,
    day             date        not null,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    started_at      timestamptz not null,
    ended_at        timestamptz not null,
    starts_with     text        not null,
    track_ids       uuid[]      not null,
    place           text        null,
    activity        text        not null check (activity in ('conversation', 'media', 'noise')),
    media_share     real        not null check (media_share >= 0 and media_share <= 1),
    noise           boolean     not null,
    title           text        null,
    summary         text        null,
    version         int         not null,
    lines_hash      text        not null,
    signature       text        not null,
    ai_status       text        not null default 'none' check (ai_status in ('none', 'done', 'skipped', 'failed')),
    ai_lines_hash   text        null,
    ai_message      text        null,
    ai_failures     int         not null default 0,
    ai_updated_at   timestamptz null,
    built_at        timestamptz not null
);

-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create unique index situations_start on situations (conversation_id, started_at);
-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index situations_day on situations (day);

-- The lines of a situation: every line of its conversation from its start to the next situation's, media and silence included.
-- A line is in one situation at most.
create table situation_segments (
    situation_id uuid   not null references situations (id) on delete cascade,
    segment_id   bigint not null references segments (id) on delete cascade,
    primary key (segment_id)
);

-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index situation_segments_situation on situation_segments (situation_id);

-- Where a day stands: cut (links within the situations are being asked, the tracks of the day will be built again, then the day is
-- cut once more) or final. built_at is the last build, so a conversation enriched after it brings the day back.
create table situation_days (
    day      date        primary key,
    stage    text        not null check (stage in ('cut', 'final')),
    built_at timestamptz not null
);
