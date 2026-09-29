-- jobs is hot: give up rather than queue behind a long transaction while the server holds locks.
set local lock_timeout = '5s';

-- Lower runs first. 0 is live audio and every other kind of job; 1 is late audio (a stored session
-- the phone uploads after reconnecting), which yields to fresh speech.
alter table jobs add column priority smallint not null default 0;

create index jobs_due_by_priority on jobs (priority, run_after, id);
