-- jobs is hot: give up rather than queue behind a long transaction while the server holds locks.
set local lock_timeout = '5s';

-- 0 is live audio and every other kind of job; 1 is late audio (a stored session the phone uploads
-- after reconnecting), which yields to fresh speech. Dequeue ranks by run_after plus a delay per
-- priority step (JobPriority.Step), so late work waits behind live jobs but cannot starve. The sum
-- is not immutable, so no index carries it; jobs_due still serves the run_after <= now filter.
alter table jobs add column priority smallint not null default 0;
