set local lock_timeout = '5s';

-- Second-brain redesign, requirement 8: every extracted item cites the lines it comes from. A source goes with its line
-- (a paid pass replaces the lines, and the item keeps no stale ids).
create table memory_sources (
    memory_id  uuid   not null references memories (id) on delete cascade,
    segment_id bigint not null references segments (id) on delete cascade,
    primary key (memory_id, segment_id)
);

create index memory_sources_segment on memory_sources (segment_id);

create table task_sources (
    task_id    uuid   not null references tasks (id) on delete cascade,
    segment_id bigint not null references segments (id) on delete cascade,
    primary key (task_id, segment_id)
);

create index task_sources_segment on task_sources (segment_id);

-- false: no line the fact cites is the wearer's. Such a memory is kept, hidden from every reader, for a later check;
-- memories from before this column count as confirmed.
alter table memories add column confirmed boolean not null default true;
