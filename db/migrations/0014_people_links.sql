set local lock_timeout = '5s';

-- docs/specs/people.md. note is the user's own words about a person, never touched by a model.
alter table people
    add column note text null check (length(note) <= 500);

-- A name set on one segment by hand, or by a confirmed suggestion: it wins over the person who owns
-- the segment's speaker_id. Deleting the person returns the segment to the voice's name.
alter table segments
    add column person_id uuid null references people (id) on delete set null;

create index segments_person_id on segments (person_id) where person_id is not null;

-- One row per conversation and kind of run (names, facts). through_segment_id is the highest
-- segments.id it read, so a run repeated over the same speech ends quietly.
create table people_runs (
    conversation_id    uuid        not null references conversations (id) on delete cascade,
    kind               text        not null check (kind in ('names', 'facts')),
    status             text        not null check (status in ('pending', 'done', 'failed')),
    through_segment_id bigint      null,
    failures           int         not null default 0,
    message            text        null,
    updated_at         timestamptz not null,
    primary key (conversation_id, kind)
);
