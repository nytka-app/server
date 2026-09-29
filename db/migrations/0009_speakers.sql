set local lock_timeout = '5s';

-- speaker_id is the provider's stable id for one voice (the stt service keeps it across chunks); null
-- when the provider gave none. is_user is null when the provider did not say, true for the wearer.
alter table segments
    add column speaker_id text    null,
    add column is_user    boolean null;

create index segments_speaker_id on segments (speaker_id) where speaker_id is not null;

-- A person is a name the user gave to one or more voices. Segments are resolved through
-- person_voices when read, so naming a voice renames its past segments too, and deleting a person
-- leaves the segments with their raw labels.
create table people (
    id         uuid        primary key,
    name       text        not null check (length(name) between 1 and 80),
    created_at timestamptz not null
);

create unique index people_name on people (lower(name));

create table person_voices (
    speaker_id text        primary key,
    person_id  uuid        not null references people (id) on delete cascade,
    created_at timestamptz not null
);

create index person_voices_person on person_voices (person_id);
