set local lock_timeout = '5s';

-- Step 5 (docs/specs/redesign step 5): the wearer's own mark on how much an item matters, which enrichment never overwrites,
-- and the lines a note cites, as tasks and memories already do (0044).
alter table tasks add column salience_manual boolean not null default false;
alter table memories add column salience_manual boolean not null default false;
alter table notes add column salience_manual boolean not null default false;

create table note_sources (
    note_id    uuid   not null references notes (id) on delete cascade,
    segment_id bigint not null references segments (id) on delete cascade,
    primary key (note_id, segment_id)
);

create index note_sources_segment on note_sources (segment_id);
