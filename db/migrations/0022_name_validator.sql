set local lock_timeout = '5s';

-- docs/specs/people.md, Layer 1 (Validation). The version of the name rules a names run applied; 0 is a run made before
-- the rules existed. POST /people/backfill?force=true queues the runs below the current version again.
alter table people_runs
    add column validator int not null default 0;
