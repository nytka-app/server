set local lock_timeout = '5s';

-- Layer 2 of docs/specs/people.md. Needs 0015: it adds the foreign key on name_suggestions.group_id.
-- A vector (centroid) is 192 little-endian float4s, 768 bytes, and never leaves Postgres.

-- The fingerprints of unnamed voices that are alike. A group lives only while a fingerprint is in it:
-- retention and deleting a conversation delete the groups they leave empty. centroid is the running mean.
create table voice_groups (
    id            uuid        primary key,
    model         text        not null,
    centroid      bytea       not null,
    count         int         not null check (count > 0),
    skipped_until timestamptz null,
    created_at    timestamptz not null,
    updated_at    timestamptz not null
);

-- The one vector of another person that outlives audio, kept once the owner confirmed the voice.
create table person_voiceprints (
    person_id  uuid        primary key references people (id) on delete cascade,
    model      text        not null,
    centroid   bytea       not null,
    count      int         not null check (count > 0),
    updated_at timestamptz not null
);

-- A voice that sounds like a person's voiceprint: a suggestion until confirmed. similarity is the best of its segments.
create table voice_matches (
    id              uuid        primary key,
    conversation_id uuid        not null references conversations (id) on delete cascade,
    person_id       uuid        not null references people (id) on delete cascade,
    segment_ids     bigint[]    not null,
    similarity      real        not null,
    status          text        not null default 'pending' check (status in ('pending', 'accepted', 'rejected')),
    skipped_until   timestamptz null,
    created_at      timestamptz not null,
    decided_at      timestamptz null,
    unique (conversation_id, person_id)
);

-- grouped: the grouping job has looked at the fingerprint (it joined a group, started one or became a match).
alter table segment_fingerprints
    add column group_id uuid    null references voice_groups (id) on delete set null,
    add column grouped  boolean not null default false;

create index segment_fingerprints_group on segment_fingerprints (group_id) where group_id is not null;
create index segment_fingerprints_ungrouped on segment_fingerprints (segment_id) where not grouped;

alter table name_suggestions
    add constraint name_suggestions_group_id_fkey foreign key (group_id) references voice_groups (id) on delete cascade;
