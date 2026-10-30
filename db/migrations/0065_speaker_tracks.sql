set local lock_timeout = '5s';

-- Speaker tracks (epic nytka-app/server#296, A5): one track per voice per local day, built by the build-tracks job from the
-- day's fingerprints. A vector (centroid) is 192 little-endian float4s, 768 bytes, and never leaves Postgres. A rebuild keeps the
-- id of a track whose lines mostly stay together, so person, dismissed and skipped_until survive it. ordinal numbers the day's
-- tracks by first line ("Voice n"). segments is the number of lines assigned, seconds their speech time.
create table speaker_tracks (
    id            uuid        primary key,
    day           date        not null,
    model         text        not null,
    centroid      bytea       not null,
    seconds       real        not null,
    segments      int         not null,
    ordinal       int         not null,
    person_id     uuid        null references people (id) on delete set null,
    dismissed     boolean     not null default false,
    skipped_until timestamptz null,
    built_at      timestamptz not null
);

-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index speaker_tracks_day on speaker_tracks (day);
-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index speaker_tracks_person on speaker_tracks (person_id) where person_id is not null;

-- The line's track. Readers follow it only while the applied tracks.enabled (tracks_state) is true.
alter table segments add column track_id uuid null;
alter table segments add constraint segments_track_id_fkey
    foreign key (track_id) references speaker_tracks (id) on delete set null not valid;
alter table segments validate constraint segments_track_id_fkey;

-- The column is new and null in every row, so the partial index builds empty.
-- squawk-ignore require-concurrent-index-creation
create index segments_track_id on segments (track_id) where track_id is not null;

-- The effective tracks.enabled the readers follow, written by the scheduler (the environment can set the setting, which no
-- query sees), as speech_state does for the speech mode. One row at most.
create table tracks_state (
    id         smallint    primary key check (id = 1),
    enabled    boolean     not null,
    updated_at timestamptz not null
);

-- One fingerprint per window of a line (a line over 30 s is cut into windows, any other is one window), made whether or not a
-- wearer voiceprint exists. Like segment_fingerprints it lives only as long as its batch's speech audio.
create table track_prints (
    segment_id  bigint      not null references segments (id) on delete cascade,
    idx         smallint    not null,
    batch_id    bigint      not null references transcription_batches (id) on delete cascade,
    model       text        not null,
    fingerprint bytea       not null,
    created_at  timestamptz not null,
    primary key (segment_id, idx)
);

-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index track_prints_batch on track_prints (batch_id);

-- The owner's own answers about two lines: the same voice or two voices. They are must-link and cannot-link constraints on every
-- rebuild. segment_id is the lower id. A paid pass moves an answer to the lines that replace its lines.
create table track_answers (
    segment_id       bigint      not null references segments (id) on delete cascade,
    other_segment_id bigint      not null references segments (id) on delete cascade,
    answer           text        not null check (answer in ('same', 'different')),
    answered_at      timestamptz not null,
    primary key (segment_id, other_segment_id),
    check (segment_id < other_segment_id)
);

-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create index track_answers_other on track_answers (other_segment_id);

-- A track that sounds like a person's voiceprint (kind 'person') or like a track of an earlier day (kind 'track'): a suggestion until
-- decided, never a merge. similarity is the cosine of the centroids.
create table track_suggestions (
    id             uuid        primary key,
    track_id       uuid        not null references speaker_tracks (id) on delete cascade,
    kind           text        not null check (kind in ('person', 'track')),
    person_id      uuid        null references people (id) on delete cascade,
    other_track_id uuid        null references speaker_tracks (id) on delete cascade,
    similarity     real        not null,
    status         text        not null default 'pending' check (status in ('pending', 'accepted', 'rejected')),
    created_at     timestamptz not null,
    decided_at     timestamptz null,
    check ((kind = 'person' and person_id is not null and other_track_id is null)
        or (kind = 'track' and other_track_id is not null and person_id is null))
);

-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create unique index track_suggestions_person on track_suggestions (track_id, person_id) where kind = 'person';
-- The table was created in this migration and is empty.
-- squawk-ignore require-concurrent-index-creation
create unique index track_suggestions_track on track_suggestions (track_id, other_track_id) where kind = 'track';
